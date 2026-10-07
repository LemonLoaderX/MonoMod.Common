# Agent Note: Match CoreCLR's atomic MethodDesc flag updates

Status: implemented

## Problem

The .NET 11 platform sets mdfNotInline with a non-atomic ushort read/modify/write.
CoreCLR modifies other bits concurrently. Its MethodDesc::InterlockedUpdateFlags
contract requires an atomic operation on the containing aligned DWORD.

## Decision

SetNotInline updates the word at MethodDesc + 4 using compare/exchange and retries
with the observed word. The m_wFlags mask at offset 6 accounts for byte order;
both existing flags and the adjacent slot survive concurrent changes. Only the
selected .NET 11 platform changes; older layout adapters keep their own contracts.

## Alternatives considered

- Interlocked.Or directly expresses the operation but is unavailable in the older
  reference targets used to build this fork. CompareExchange has the same required
  atomicity without raising the framework requirement.
- A managed lock would serialize our writers, but CoreCLR does not take that lock.
  It cannot protect against runtime updates.

## Consequences

The helper assumes the selected runtime's aligned MethodDesc layout. It does not
extend the supported JIT GUID set. The consuming tests/Arm64Precode fixture runs
the real helper on synthetic storage while a second thread sets disjoint bits in
the flags and adjacent slot, with canaries on both sides. It never writes foreign
.NET 11 layout assumptions into the .NET 10 test host's MethodDesc.

## Prior-note audit

The existing native exception-slot note is unrelated and retained. No previous
flag-update note exists; PATCHES.md owns the selected runtime layout/GUID context.
