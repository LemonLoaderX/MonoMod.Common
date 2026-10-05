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
            ushort* flags = (ushort*)((byte*)handle.Value + 6);
            *flags |= 0x2000;
        }

        protected override unsafe void InstallJitHooks(IntPtr jit) {
            IntPtr* slot = GetVTableEntry(jit, VTableIndex_ICorJitCompiler_compileMethod);
            original = (*slot).AsDelegate<CompileMethod>();
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
            DetourHelper.Native.MakeWritable((IntPtr)slot, (uint)IntPtr.Size);
            Interlocked.Exchange(ref *slot, callback);
        }

        private int Compile(IntPtr jit, IntPtr info, IntPtr methodInfo, uint flags,
            out IntPtr entry, out uint size) {
            entry = IntPtr.Zero;
            size = 0;
            if (jit == IntPtr.Zero)
                return 0;

            int error = Marshal.GetLastWin32Error();
            depth++;
            try {
                int result = original(jit, info, methodInfo, flags, out entry, out size);
                if (result == 0 && depth == 1 && entry != IntPtr.Zero) {
                    try {
                        MethodBase method;
                        lock (methodsLock)
                            methods.TryGetValue(Marshal.ReadIntPtr(methodInfo), out method);
                        OnMethodCompiledEvent handlers = OnMethodCompiled;
                        if (method != null && handlers != null) {
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
