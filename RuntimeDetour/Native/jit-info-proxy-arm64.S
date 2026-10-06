#include "jit-info-layout.h"
.arch armv8-a
.altmacro
.macro FORWARD index
    .text
    .p2align 2
proxy_\index:
    ldr x0, [x0, #8]
    ldr x9, [x0]
    ldr x9, [x9, #(\index * 8)]
    br x9
    .section .data.rel.ro,"aw"
    .if \index == MM_JIT_ALLOC_MEM_SLOT
        .quad mm_jit_alloc_mem
    .else
        .quad proxy_\index
    .endif
.endm
.section .data.rel.ro,"aw"
.p2align 3
.global mm_jit_info_vtable
mm_jit_info_vtable:
.set slot, 0
.rept MM_JIT_INFO_SLOTS
    FORWARD %slot
    .set slot, slot + 1
.endr
.section .note.GNU-stack,"",%progbits
