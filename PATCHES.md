# CoreCLR compatibility patches

This branch extends upstream commit
`ea24867bb49621372eaf53b2ef552cbf488af55b` for modern CoreCLR hosts.

`DynamicMethod` stores its return type in the private `_returnType` field on
.NET 10. `DMDEmitDynamicMethodGenerator` now checks that name after the existing
runtime-specific names. The fallback preserves behavior on older runtimes and
avoids downstream assembly rewriting.

`LocalBuilder` is abstract on modern CoreCLR. `CecilILGenerator` now resolves the
runtime's concrete local-builder implementation before invoking its internal
constructor, with a generated-local probe as a name-independent fallback. This
prevents Harmony's emitter helper from failing during static initialization on
Android CoreCLR.

Validate the patches by generating a `DynamicMethodDefinition` on .NET 10 and by
running the consuming Harmony probes against dynamic methods with local variables.

On the pinned .NET 11 main baseline, IRuntimeMethodInfo exposes a nonpublic
static GetValue method instead of the older instance get_Value accessor.
DetourRuntimeNETPlatform supports both shapes without rewriting assemblies.
This path passed the consuming .NET 10 probes and the .NET 11 Android/Bionic
real-game Harmony and Mod startup probes.

Upstream JIT hooks preserve the thread's last P/Invoke error. The fork builds
against pre-.NET 6 reference assemblies, so it uses GetLastWin32Error and resolves
SetLastPInvokeError once from the running runtime. Older runtimes without that
setter retain their previous behavior; modern runtimes restore the value even
when the hook unwinds through an exception.

## Modern ARM64 CoreCLR precodes and recompilation

The ARM64 walker recognizes page-separated FixupPrecode and ordinary StubPrecode
using the full instruction template and owning MethodDesc. It reads the actual
body target instead of patching an entry stub that compiled callers may bypass.
PInvoke, interpreter and return-buffer adapters remain intact. An unprepared fixup
gets one preparation retry; a still unprepared method raises a managed error.

The .NET 11 ARM64 JIT GUID selects a dedicated platform which forwards compilation
requests unchanged and notifies existing detours when a pinned method is compiled
again. It uses the request's MethodDesc identity rather than legacy signature or
RuntimeAssembly layouts. Error helpers and delegate thunks are warmed before
vtable installation to avoid recursive compilation, and callback notifications
preserve last P/Invoke error state, isolate failing subscribers and contain
diagnostic-writer exceptions. Index publication checks the live pin count under
its lock after preparation so concurrent final Unpin cannot leave a stale entry.

Modern Unix hosts resolve the JIT beside CoreLib without enumerating Process.Modules.
Consumers must select the factory before creating detours. Unknown GUIDs retain
the existing fallback; this adaptation does not assert support for every .NET 11
JIT revision. Host template regressions are in the consuming MonoMod fork's
tests/Arm64Precode; actual patch/hot-call/unpatch behavior requires ARM64 device
acceptance through the Loader smoke Mod.
The factory retains and reuses the installed .NET 11 hook owner: a native callback
pointer alone cannot keep its managed delegate alive. Remove these adaptations
when the consumed upstream provides equivalent precode decoding and matching
.NET 11 JIT support, after rerunning the same host and ARM64 device regressions.
