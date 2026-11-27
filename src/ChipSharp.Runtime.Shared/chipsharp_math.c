// ChipSharp runtime, shared by every target: the transcendental functions of System.Math and System.MathF (and
// FusedMultiplyAdd), as near to what .NET computes on a PC as one implementation for every target gets.
//
//   ChipSharp_Runtime_<Name>Float64    Math.<Name>(double), e.g. ChipSharp_Runtime_SinFloat64 is Math.Sin
//   ChipSharp_Runtime_<Name>Float32    MathF.<Name>(float), e.g. ChipSharp_Runtime_SinFloat32 is MathF.Sin
//
// .NET does not compute these itself: on Windows it calls the C runtime (UCRT), whose x64 functions come from AMD's
// math library, some in C and some in assembly that uses the processor's fused multiply-add; on Linux it calls glibc.
// Their results are not always correctly rounded, so no implementation gives .NET's results bit for bit unless it
// computes as they do. What each function here is, measured against .NET 10 on Windows x64 (MathAccuracyTests keeps
// the numbers, the README lists them):
//
//   - AMD's reference C (chipsharp_math_amd.c), which the UCRT has: Asin, Acos, Atan, Atan2, Sinh, Cosh, Tanh, and of
//     MathF also Tan. Without the fused multiply-adds the UCRT's build of them has, they give .NET's result for 98
//     to 100 % of the values (Math.Cosh: 97.9 %);
//   - the formulas the UCRT computes Asinh, Acosh and Atanh with, over Log1p and Log (found by measuring), whose
//     results follow musl's Log1p: .NET's for 88 to 94 % of the values;
//   - musl's (chipsharp_math_musl.c) where .NET runs assembly: Sin, Cos, Tan, Exp, Log, Log2, Log10, Pow, Cbrt,
//     Log1p, and their MathF twins but Tan. musl's are within 1 ulp of the exact result, like .NET's, so the two
//     agree for 96.9 to 100 % of the values (Cbrt: 70 %).
//
// Every one computes with + - * / and integer operations only, each rounded as IEEE 754 says (none contracted into a
// fused multiply-add), so a program gets the same bits on the PC (SSE2) and on a chip without float hardware
// (compiler-rt's soft float). NaN as .NET has it on x64: a NaN argument is the result, made quiet; an argument outside
// the function's domain (Sqrt of -1, Sin of infinity, Log of -1) gives the processor's default NaN, 0xFFF8000000000000
// (0xFFC00000 for a float), whose sign bit is set. The functions of MathF compute with floats where their algorithm
// does, and in double precision where it needs that for a float result (musl's sinf, expf, logf and powf evaluate a
// short polynomial in doubles), never with the double function and a rounding.

#pragma STDC FP_CONTRACT OFF

// 32 bits on every target: an int or a long (AVR's int has 16), as the compiler's own fixed-width types say
typedef __UINT32_TYPE__ u32;
typedef unsigned long long u64;

static u64 bits64(double value)
{
    union { double d; u64 u; } v = { value };
    return v.u;
}

static double float64(u64 bits)
{
    union { u64 u; double d; } v = { bits };
    return v.d;
}

static u32 bits32(float value)
{
    union { float f; u32 u; } v = { value };
    return v.u;
}

static float float32(u32 bits)
{
    union { u32 u; float f; } v = { bits };
    return v.f;
}

// The result of a function of x: a NaN x as itself, quiet; any other NaN as the processor's default NaN.
static double result64(double result, double x)
{
    if (result == result)
    {
        return result;
    }

    return x != x ? float64(bits64(x) | 0x0008000000000000ULL) : float64(0xfff8000000000000ULL);
}

static double result64_2(double result, double x, double y)
{
    if (result == result)
    {
        return result;
    }

    return x != x ? float64(bits64(x) | 0x0008000000000000ULL)
        : y != y ? float64(bits64(y) | 0x0008000000000000ULL)
        : float64(0xfff8000000000000ULL);
}

static float result32(float result, float x)
{
    if (result == result)
    {
        return result;
    }

    return x != x ? float32(bits32(x) | 0x00400000U) : float32(0xffc00000U);
}

static float result32_2(float result, float x, float y)
{
    if (result == result)
    {
        return result;
    }

    return x != x ? float32(bits32(x) | 0x00400000U)
        : y != y ? float32(bits32(y) | 0x00400000U)
        : float32(0xffc00000U);
}

// chipsharp_math_musl.c
double chipsharp_musl_sin(double x);
double chipsharp_musl_cos(double x);
double chipsharp_musl_tan(double x);
double chipsharp_musl_exp(double x);
double chipsharp_musl_log(double x);
double chipsharp_musl_log2(double x);
double chipsharp_musl_log10(double x);
double chipsharp_musl_pow(double x, double y);
double chipsharp_musl_cbrt(double x);
double chipsharp_musl_log1p(double x);
double chipsharp_musl_fma(double x, double y, double z);
float chipsharp_musl_sinf(float x);
float chipsharp_musl_cosf(float x);
float chipsharp_musl_expf(float x);
float chipsharp_musl_logf(float x);
float chipsharp_musl_log2f(float x);
float chipsharp_musl_log10f(float x);
float chipsharp_musl_powf(float x, float y);
float chipsharp_musl_cbrtf(float x);
float chipsharp_musl_log1pf(float x);
float chipsharp_musl_fmaf(float x, float y, float z);

// chipsharp_math_amd.c
double chipsharp_amd_asin(double x);
double chipsharp_amd_acos(double x);
double chipsharp_amd_atan(double x);
double chipsharp_amd_atan2(double y, double x);
double chipsharp_amd_sinh(double x);
double chipsharp_amd_cosh(double x);
double chipsharp_amd_tanh(double x);
float chipsharp_amd_tanf(float x);
float chipsharp_amd_asinf(float x);
float chipsharp_amd_acosf(float x);
float chipsharp_amd_atanf(float x);
float chipsharp_amd_atan2f(float y, float x);
float chipsharp_amd_sinhf(float x);
float chipsharp_amd_coshf(float x);
float chipsharp_amd_tanhf(float x);

// chipsharp_float_math.c
double ChipSharp_Runtime_SqrtFloat64(double value);
float ChipSharp_Runtime_SqrtFloat32(float value);

#define LN2 6.93147180559945286227e-01
#define LN2F 6.9314718246e-01f

// ---- Trigonometry ---------------------------------------------------------------------------------------------------

double ChipSharp_Runtime_SinFloat64(double x) { return result64(chipsharp_musl_sin(x), x); }
double ChipSharp_Runtime_CosFloat64(double x) { return result64(chipsharp_musl_cos(x), x); }
double ChipSharp_Runtime_TanFloat64(double x) { return result64(chipsharp_musl_tan(x), x); }
double ChipSharp_Runtime_AsinFloat64(double x) { return result64(chipsharp_amd_asin(x), x); }
double ChipSharp_Runtime_AcosFloat64(double x) { return result64(chipsharp_amd_acos(x), x); }
double ChipSharp_Runtime_AtanFloat64(double x) { return result64(chipsharp_amd_atan(x), x); }
double ChipSharp_Runtime_Atan2Float64(double y, double x) { return result64_2(chipsharp_amd_atan2(y, x), y, x); }

float ChipSharp_Runtime_SinFloat32(float x) { return result32(chipsharp_musl_sinf(x), x); }
float ChipSharp_Runtime_CosFloat32(float x) { return result32(chipsharp_musl_cosf(x), x); }
float ChipSharp_Runtime_TanFloat32(float x) { return result32(chipsharp_amd_tanf(x), x); }
float ChipSharp_Runtime_AsinFloat32(float x) { return result32(chipsharp_amd_asinf(x), x); }
float ChipSharp_Runtime_AcosFloat32(float x) { return result32(chipsharp_amd_acosf(x), x); }
float ChipSharp_Runtime_AtanFloat32(float x) { return result32(chipsharp_amd_atanf(x), x); }
float ChipSharp_Runtime_Atan2Float32(float y, float x) { return result32_2(chipsharp_amd_atan2f(y, x), y, x); }

// ---- Hyperbolic functions -------------------------------------------------------------------------------------------

double ChipSharp_Runtime_SinhFloat64(double x) { return result64(chipsharp_amd_sinh(x), x); }
double ChipSharp_Runtime_CoshFloat64(double x) { return result64(chipsharp_amd_cosh(x), x); }
double ChipSharp_Runtime_TanhFloat64(double x) { return result64(chipsharp_amd_tanh(x), x); }

float ChipSharp_Runtime_SinhFloat32(float x) { return result32(chipsharp_amd_sinhf(x), x); }
float ChipSharp_Runtime_CoshFloat32(float x) { return result32(chipsharp_amd_coshf(x), x); }
float ChipSharp_Runtime_TanhFloat32(float x) { return result32(chipsharp_amd_tanhf(x), x); }

// Math.Asinh, as the UCRT computes it: log1p(|x| + x^2 / (1 + sqrt(1 + x^2))), and log|x| + ln 2 above 2^28, where
// the square no longer matters; the sign of x on the result (-0 for -0).
double ChipSharp_Runtime_AsinhFloat64(double x)
{
    double a = x < 0 ? -x : x;
    double r;
    if (a != a || a == __builtin_inf())
    {
        return result64(x + x, x);
    }

    if (a > 0x1p28)
    {
        r = chipsharp_musl_log(a) + LN2;
    }
    else
    {
        r = chipsharp_musl_log1p(a + a * a / (1.0 + ChipSharp_Runtime_SqrtFloat64(1.0 + a * a)));
    }

    return bits64(x) >> 63 ? -r : r;
}

// Math.Acosh: below 2, log1p(t + sqrt(t (2 + t))) with t = x - 1, exact near 1; then log(x + sqrt(x^2 - 1)); above
// 2^28, log x + ln 2. NaN below 1.
double ChipSharp_Runtime_AcoshFloat64(double x)
{
    if (!(x >= 1.0))
    {
        return result64(x != x ? x : __builtin_nan(""), x);
    }

    if (x < 2.0)
    {
        double t = x - 1.0;
        return chipsharp_musl_log1p(t + ChipSharp_Runtime_SqrtFloat64(t * (2.0 + t)));
    }

    if (x > 0x1p28)
    {
        return x == __builtin_inf() ? x : chipsharp_musl_log(x) + LN2;
    }

    return chipsharp_musl_log(x + ChipSharp_Runtime_SqrtFloat64(x * x - 1.0));
}

// Math.Atanh: log1p(2 |x| / (1 - |x|)) / 2, with the sign of x; infinity at 1, NaN beyond it.
double ChipSharp_Runtime_AtanhFloat64(double x)
{
    double a = x < 0 ? -x : x;
    if (!(a <= 1.0))
    {
        return result64(x != x ? x : __builtin_nan(""), x);
    }

    double r = 0.5 * chipsharp_musl_log1p(2.0 * a / (1.0 - a));
    return bits64(x) >> 63 ? -r : r;
}

// MathF.Asinh: the same as Math.Asinh, in floats.
float ChipSharp_Runtime_AsinhFloat32(float x)
{
    float a = x < 0 ? -x : x;
    float r;
    if (a != a || a == __builtin_inff())
    {
        return result32(x + x, x);
    }

    if (a > 0x1p28f)
    {
        r = chipsharp_musl_logf(a) + LN2F;
    }
    else
    {
        r = chipsharp_musl_log1pf(a + a * a / (1.0f + ChipSharp_Runtime_SqrtFloat32(1.0f + a * a)));
    }

    return bits32(x) >> 31 ? -r : r;
}

float ChipSharp_Runtime_AcoshFloat32(float x)
{
    if (!(x >= 1.0f))
    {
        return result32(x != x ? x : __builtin_nanf(""), x);
    }

    if (x < 2.0f)
    {
        float t = x - 1.0f;
        return chipsharp_musl_log1pf(t + ChipSharp_Runtime_SqrtFloat32(t * (2.0f + t)));
    }

    if (x > 0x1p28f)
    {
        return x == __builtin_inff() ? x : chipsharp_musl_logf(x) + LN2F;
    }

    return chipsharp_musl_logf(x + ChipSharp_Runtime_SqrtFloat32(x * x - 1.0f));
}

float ChipSharp_Runtime_AtanhFloat32(float x)
{
    float a = x < 0 ? -x : x;
    if (!(a <= 1.0f))
    {
        return result32(x != x ? x : __builtin_nanf(""), x);
    }

    float r = 0.5f * chipsharp_musl_log1pf(2.0f * a / (1.0f - a));
    return bits32(x) >> 31 ? -r : r;
}

// ---- Exponentials, logarithms, powers ---------------------------------------------------------------------------------

double ChipSharp_Runtime_ExpFloat64(double x) { return result64(chipsharp_musl_exp(x), x); }
double ChipSharp_Runtime_LogFloat64(double x) { return result64(chipsharp_musl_log(x), x); }
double ChipSharp_Runtime_Log2Float64(double x) { return result64(chipsharp_musl_log2(x), x); }
double ChipSharp_Runtime_Log10Float64(double x) { return result64(chipsharp_musl_log10(x), x); }
double ChipSharp_Runtime_PowFloat64(double x, double y) { return result64_2(chipsharp_musl_pow(x, y), x, y); }
double ChipSharp_Runtime_CbrtFloat64(double x) { return result64(chipsharp_musl_cbrt(x), x); }

float ChipSharp_Runtime_ExpFloat32(float x) { return result32(chipsharp_musl_expf(x), x); }
float ChipSharp_Runtime_LogFloat32(float x) { return result32(chipsharp_musl_logf(x), x); }
float ChipSharp_Runtime_Log2Float32(float x) { return result32(chipsharp_musl_log2f(x), x); }
float ChipSharp_Runtime_Log10Float32(float x) { return result32(chipsharp_musl_log10f(x), x); }
float ChipSharp_Runtime_PowFloat32(float x, float y) { return result32_2(chipsharp_musl_powf(x, y), x, y); }
float ChipSharp_Runtime_CbrtFloat32(float x) { return result32(chipsharp_musl_cbrtf(x), x); }

// ---- FusedMultiplyAdd: x * y + z rounded once, exactly (musl's fma and fmaf, in integers) ---------------------------

double ChipSharp_Runtime_FusedMultiplyAddFloat64(double x, double y, double z)
{
    double r = chipsharp_musl_fma(x, y, z);
    if (r == r)
    {
        return r;
    }

    return x != x ? float64(bits64(x) | 0x0008000000000000ULL)
        : y != y ? float64(bits64(y) | 0x0008000000000000ULL)
        : z != z ? float64(bits64(z) | 0x0008000000000000ULL)
        : float64(0xfff8000000000000ULL);
}

float ChipSharp_Runtime_FusedMultiplyAddFloat32(float x, float y, float z)
{
    float r = chipsharp_musl_fmaf(x, y, z);
    if (r == r)
    {
        return r;
    }

    return x != x ? float32(bits32(x) | 0x00400000U)
        : y != y ? float32(bits32(y) | 0x00400000U)
        : z != z ? float32(bits32(z) | 0x00400000U)
        : float32(0xffc00000U);
}
