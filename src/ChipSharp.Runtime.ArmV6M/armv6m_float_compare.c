// ChipSharp runtime for ARMv6-M chips (Cortex-M0 and M0+): the float and double comparisons LLVM calls.
//
// ARMv6-M has no float hardware, so LLVM turns float and double arithmetic, comparisons and conversions into calls
// to the ARM run-time ABI (RTABI) functions: __aeabi_fadd, __aeabi_dmul, __aeabi_f2iz, __aeabi_l2d, ... Those come
// from LLVM's compiler-rt, unchanged, in CompilerRt/ (Apache-2.0 WITH LLVM-exception, CompilerRt/LICENSE.TXT): every
// one rounds to nearest exactly as IEEE 754 says, with subnormal numbers, infinities and NaN, so results match a PC's
// float hardware and .NET bit for bit. compiler-rt has these comparisons only in ARM assembly; here they are in C, on
// top of its __lesf2, __gesf2, __eqsf2 and the double versions (CompilerRt/comparesf2.c, comparedf2.c):
//
//   __aeabi_fcmpeq, __aeabi_fcmplt, __aeabi_fcmple, __aeabi_fcmpge, __aeabi_fcmpgt   1 when the comparison holds,
//   __aeabi_dcmpeq, __aeabi_dcmplt, __aeabi_dcmple, __aeabi_dcmpge, __aeabi_dcmpgt   0 when not or when unordered
//
// (__aeabi_fcmpun and __aeabi_dcmpun, 1 when either value is NaN, are in compiler-rt's C.)

// compiler-rt: __eqsf2 and __lesf2 give -1 (less), 0 (equal) or 1 (greater or unordered); __gesf2 gives -1 (less or
// unordered), 0 (equal) or 1 (greater). Their type is a long (a long long where a long is 32 bits but a pointer 64,
// as on 64-bit Windows, where the tests build this file to check it).
#if __SIZEOF_POINTER__ == 8 && __SIZEOF_LONG__ == 4
typedef long long compare_result;
#else
typedef long compare_result;
#endif

compare_result __eqsf2(float a, float b);
compare_result __lesf2(float a, float b);
compare_result __gesf2(float a, float b);
compare_result __eqdf2(double a, double b);
compare_result __ledf2(double a, double b);
compare_result __gedf2(double a, double b);

int __aeabi_fcmpeq(float a, float b)
{
    return __eqsf2(a, b) == 0;
}

int __aeabi_fcmplt(float a, float b)
{
    return __lesf2(a, b) < 0;
}

int __aeabi_fcmple(float a, float b)
{
    return __lesf2(a, b) <= 0;
}

int __aeabi_fcmpge(float a, float b)
{
    return __gesf2(a, b) >= 0;
}

int __aeabi_fcmpgt(float a, float b)
{
    return __gesf2(a, b) > 0;
}

int __aeabi_dcmpeq(double a, double b)
{
    return __eqdf2(a, b) == 0;
}

int __aeabi_dcmplt(double a, double b)
{
    return __ledf2(a, b) < 0;
}

int __aeabi_dcmple(double a, double b)
{
    return __ledf2(a, b) <= 0;
}

int __aeabi_dcmpge(double a, double b)
{
    return __gedf2(a, b) >= 0;
}

int __aeabi_dcmpgt(double a, double b)
{
    return __gedf2(a, b) > 0;
}
