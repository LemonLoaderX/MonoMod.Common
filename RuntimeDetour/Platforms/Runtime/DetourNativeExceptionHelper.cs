using System;
using System.IO;
using System.Runtime.InteropServices;
using MonoMod.Utils;

namespace MonoMod.RuntimeDetour.Platforms {
    // ARM64 POSIX transitions adapted from upstream MonoMod's exception helper.
    // Native exceptions must be rethrown outside the managed compiler callback.
    internal sealed class DetourNativeExceptionHelper : IDisposable {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr GetExceptionSlot();

        private readonly GetExceptionSlot getExceptionSlot;
        private readonly IntPtr managedToNative;
        private readonly IntPtr nativeToManaged;
        private IntPtr library;
        private IntPtr managedToNativeStub;
        private IntPtr nativeToManagedStub;

        public DetourNativeExceptionHelper() {
            string path = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(),
                "monomod-exception-" + Guid.NewGuid().ToString("N") + ".so");
            try {
                using (Stream resource = typeof(DetourNativeExceptionHelper).Assembly.GetManifestResourceStream(
                    "MonoMod.RuntimeDetour.NativeExceptionHelper.so")) {
                    if (resource == null)
                        throw new PlatformNotSupportedException("The ARM64 native exception helper was not embedded in this build.");
                    using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) {
                        byte[] buffer = new byte[4096];
                        int count;
                        while ((count = resource.Read(buffer, 0, buffer.Length)) != 0)
                            output.Write(buffer, 0, count);
                    }
                }
                library = DynDll.OpenLibrary(path);
                getExceptionSlot = library.GetFunction("eh_get_exception_ptr").AsDelegate<GetExceptionSlot>();
                managedToNative = library.GetFunction("eh_managed_to_native");
                nativeToManaged = library.GetFunction("eh_native_to_managed");
                // Warm the marshaling wrapper before publishing the JIT hook.
                GetSlot();
                File.Delete(path);
            } catch {
                Dispose();
                throw;
            } finally {
                if (File.Exists(path))
                    File.Delete(path);
            }
            // The process-owned JIT hook keeps this library and its stubs alive.
        }

        public IntPtr GetSlot() => getExceptionSlot();

        public IntPtr WrapManagedToNative(IntPtr target) => managedToNativeStub = CreateStub(managedToNative, target);
        public IntPtr WrapNativeToManaged(IntPtr target) => nativeToManagedStub = CreateStub(nativeToManaged, target);

        public void Dispose() {
            if (managedToNativeStub != IntPtr.Zero) {
                DetourHelper.Native.MemFree(managedToNativeStub);
                managedToNativeStub = IntPtr.Zero;
            }
            if (nativeToManagedStub != IntPtr.Zero) {
                DetourHelper.Native.MemFree(nativeToManagedStub);
                nativeToManagedStub = IntPtr.Zero;
            }
            if (library != IntPtr.Zero) {
                DynDll.CloseLibrary(library);
                library = IntPtr.Zero;
            }
        }

        private static IntPtr CreateStub(IntPtr helper, IntPtr target) {
            IntPtr stub = DetourHelper.Native.MemAlloc(32);
            try {
                DetourHelper.Native.MakeWritable(stub, 32);
                Marshal.WriteInt32(stub, 0, unchecked((int)0x58000089)); // ldr x9, #16
                Marshal.WriteInt32(stub, 4, unchecked((int)0x580000AA)); // ldr x10, #24
                Marshal.WriteInt32(stub, 8, unchecked((int)0xD61F0140)); // br x10
                Marshal.WriteInt32(stub, 12, unchecked((int)0xD503201F));
                Marshal.WriteIntPtr(stub, 16, target);
                Marshal.WriteIntPtr(stub, 24, helper);
                DetourHelper.Native.MakeExecutable(stub, 32);
                DetourHelper.Native.FlushICache(stub, 32);
                return stub;
            } catch {
                DetourHelper.Native.MemFree(stub);
                throw;
            }
        }
    }
}
