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
