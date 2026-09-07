# CoreCLR compatibility patches

This branch extends upstream commit
`d679ae74d002e513bd88e52091b66283d8537d83` for .NET 10 CoreCLR hosts.

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
