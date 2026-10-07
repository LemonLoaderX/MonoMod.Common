using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using MonoMod.Utils;

namespace MonoMod.RuntimeDetour.Platforms {
#if !MONOMOD_INTERNAL
    public
#endif
    class DetourRuntimeNET110Platform : DetourRuntimeNETCorePlatform {
        public static readonly Guid JitVersionGuid = new Guid("5a3e8dc8-83bf-47e5-b032-531dbd307dbe");

        private readonly Dictionary<IntPtr, MethodBase> methods = new Dictionary<IntPtr, MethodBase>();
        private readonly object methodsLock = new object();
        private CompileMethod original;
        private CompileMethod hook;
        private DetourNativeExceptionHelper exceptionHelper;
        [ThreadStatic] private static int depth;
        private static readonly Action<int> RestoreLastPInvokeError =
            typeof(Marshal).GetMethod("SetLastPInvokeError", new[] { typeof(int) }) is MethodInfo setter
                ? (Action<int>)Delegate.CreateDelegate(typeof(Action<int>), setter)
                : _ => { };

        // Forward CORINFO_METHOD_INFO unchanged. Its first member is ftn; the
        // remaining version-specific signature layout is not needed for pinned methods.
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CompileMethod(IntPtr jit, IntPtr info, IntPtr methodInfo,
            uint flags, out IntPtr entry, out uint size);

        public override bool OnMethodCompiledWillBeCalled => true;
        public override event OnMethodCompiledEvent OnMethodCompiled;
        internal event Action<MethodBase, IntPtr, IntPtr, ulong> OnMethodCompiledWithWritableCode;

        internal void ReleaseJitHookHelpers() {
            exceptionHelper?.Dispose();
            exceptionHelper = null;
        }

        public override void Pin(MethodBase method) {
            method = GetIdentifiable(method);
            RuntimeMethodHandle handle = GetMethodHandle(method);
            base.Pin(method);
            SynchronizePin(method, handle);
        }

        public override void Unpin(MethodBase method) {
            method = GetIdentifiable(method);
            RuntimeMethodHandle handle = GetMethodHandle(method);
            base.Unpin(method);
            SynchronizePin(method, handle);
        }

        private void SynchronizePin(MethodBase method, RuntimeMethodHandle handle) {
            // Preparation can enter the JIT. Keep it outside this lock, then read
            // the live pin count inside the lock so a concurrent Unpin cannot
            // leave a stale method (or a null method under a zero handle).
            lock (methodsLock) {
                MethodPinInfo pin = GetPin(method);
                if (pin.Count > 0)
                    methods[handle.Value] = pin.Method;
                else
                    methods.Remove(handle.Value);
            }
        }

        protected override unsafe void DisableInlining(MethodBase method, RuntimeMethodHandle handle) {
            // .NET 11 MethodDesc::m_wFlags, mdfNotInline (vm/method.hpp).
            SetNotInline(handle.Value);
        }

        internal static unsafe void SetNotInline(IntPtr methodDesc) {
            // Match MethodDesc::InterlockedUpdateFlags: update the aligned DWORD
            // containing m_wFlags atomically, preserving both flags and the slot.
            ref int flagsAndSlot = ref *(int*)((byte*)methodDesc + 4);
            int mask = BitConverter.IsLittleEndian ? 0x20000000 : 0x2000;
            int observed = Volatile.Read(ref flagsAndSlot);
            while (true) {
                int previous = Interlocked.CompareExchange(ref flagsAndSlot, observed | mask, observed);
                if (previous == observed)
                    return;
                observed = previous;
            }
        }

        protected override unsafe void InstallJitHooks(IntPtr jit) {
            IntPtr* slot = GetVTableEntry(jit, VTableIndex_ICorJitCompiler_compileMethod);
            IntPtr originalPointer = *slot;
            if (!PlatformHelper.Is(Platform.Windows)) {
                exceptionHelper = new DetourNativeExceptionHelper();
                originalPointer = exceptionHelper.WrapManagedToNative(originalPointer);
            }
            original = originalPointer.AsDelegate<CompileMethod>();
            RuntimeHelpers.PrepareDelegate(original);
            RestoreLastPInvokeError(Marshal.GetLastWin32Error());
            hook = Compile;
            IntPtr callback = Marshal.GetFunctionPointerForDelegate(hook);
            // Warm the reverse-P/Invoke wrapper before replacing the JIT vtable.
            NativeDetourData trampoline = CreateNativeTrampolineTo(callback);
            try {
                trampoline.Method.AsDelegate<CompileMethod>()(IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, 0, out _, out _);
            } finally {
                FreeNativeTrampoline(trampoline);
            }
            if (exceptionHelper != null)
                callback = exceptionHelper.WrapNativeToManaged(callback);
            // The JIT vtable is data. Android rejects executable permission on
            // its file-backed read-only page even though an RW update is allowed.
            if (DetourHelper.Native is DetourNativeLibcPlatform libc)
                libc.MakeDataWritable((IntPtr)slot, (uint)IntPtr.Size);
            else
                DetourHelper.Native.MakeWritable((IntPtr)slot, (uint)IntPtr.Size);
            Interlocked.Exchange(ref *slot, callback);
        }

        private unsafe int Compile(IntPtr jit, IntPtr info, IntPtr methodInfo, uint flags,
            out IntPtr entry, out uint size) {
            entry = IntPtr.Zero;
            size = 0;
            if (jit == IntPtr.Zero)
                return 0;

            int error = Marshal.GetLastWin32Error();
            IntPtr* exceptionSlot = exceptionHelper == null ? null : (IntPtr*)exceptionHelper.GetSlot();
            IntPtr nativeException = IntPtr.Zero;
            depth++;
            try {
                IntPtr* wrapper = stackalloc IntPtr[5];
                for (int i = 0; i < 5; i++)
                    wrapper[i] = IntPtr.Zero;
                IntPtr compilerInfo = info;
                if (depth == 1 && exceptionHelper != null) {
                    wrapper[0] = exceptionHelper.JitInfoVTable;
                    wrapper[1] = info;
                    compilerInfo = (IntPtr)wrapper;
                }
                int result;
                try {
                    result = original(jit, compilerInfo, methodInfo, flags, out entry, out size);
                } catch (InvalidProgramException) when (exceptionSlot != null && *exceptionSlot == IntPtr.Zero) {
                    // CoreCLR reports invalid IL on its normal managed call path.
                    return unchecked((int)0x80000001); // CORJIT_BADCODE
                }
                if (exceptionSlot != null && (nativeException = *exceptionSlot) != IntPtr.Zero)
                    return result;
                if (result == 0 && depth == 1 && entry != IntPtr.Zero) {
                    try {
                        MethodBase method;
                        lock (methodsLock)
                            methods.TryGetValue(Marshal.ReadIntPtr(methodInfo), out method);
                        if (method == null)
                            return result;
                        IntPtr writableEntry = entry;
                        if (compilerInfo != info) {
                            long offset = entry.ToInt64() - wrapper[2].ToInt64();
                            if (wrapper[3] == IntPtr.Zero || offset < 0 ||
                                (ulong)offset + size > (ulong)wrapper[4].ToInt64())
                                throw new InvalidOperationException("The JIT did not provide a matching writable code allocation.");
                            writableEntry = new IntPtr(wrapper[3].ToInt64() + offset);
                        }
                        var writableHandlers = OnMethodCompiledWithWritableCode;
                        if (writableHandlers != null)
                            foreach (Action<MethodBase, IntPtr, IntPtr, ulong> handler in writableHandlers.GetInvocationList())
                                try {
                                    handler(method, entry, writableEntry, size);
                                } catch (Exception e) {
                                    LogNotificationError(e);
                                }
                        OnMethodCompiledEvent handlers = OnMethodCompiled;
                        if (handlers != null) {
                            foreach (OnMethodCompiledEvent handler in handlers.GetInvocationList()) {
                                try {
                                    handler(method, entry, size);
                                } catch (Exception e) {
                                    LogNotificationError(e);
                                }
                            }
                        }
                    } catch (Exception e) {
                        LogNotificationError(e);
                    }
                }
                return result;
            } finally {
                RestoreLastPInvokeError(error);
                depth--;
                if (exceptionSlot != null)
                    *exceptionSlot = nativeException;
            }
        }

        private static void LogNotificationError(Exception error) {
            try {
                MMDbgLog.Log($"Error updating a .NET 11 method detour: {error}");
            } catch {
                // Diagnostics must not throw across the native compiler callback.
            }
        }
    }
}
