// ChipSharp runtime, shared by every target: the functions of Math and MathF that come from AMD's math library, the
// one Microsoft's C runtime (UCRT) has on x64, which .NET calls on Windows. They are AMD's reference versions in C
// (aocl-libm-ose, src/ref), as the Windows build of that library compiles them (WINDOWS defined), unchanged but for
// what this file's prelude says. ChipSharp's own entry points, which call these, are in chipsharp_math.c.
//
//   double: asin, acos, atan, atan2, sinh, cosh, tanh
//   float:  asinf, acosf, atanf, atan2f, sinhf, coshf, tanhf, tanf
//
// They compute with + - * / and integer operations only, each rounded as IEEE 754 says, so the PC (SSE2) and a chip
// without float hardware (compiler-rt's soft float) get the same bits.
//
// The prelude replaces the library's headers (libm_util_amd.h, libm_inlines_amd.h, libm/alm_special.h): the names are
// ChipSharp's own (chipsharp_amd_asin, ...); exceptions and errno are left out (.NET has neither), so a special case
// returns its value; the SSE2 square root is ChipSharp's (the same correctly rounded result); and tanf reduces an
// argument of 500000 or more with musl's __rem_pio2f (chipsharp_math_musl.c) instead of AMD's assembly. License:
//
//   Copyright (C) 2008-2022, Advanced Micro Devices, Inc. All rights reserved.
//   Copyright (C) 2022-2023, Advanced Micro Devices, Inc. All rights reserved.
//
//   Redistribution and use in source and binary forms, with or without modification, are permitted provided that the
//   following conditions are met:
//   1. Redistributions of source code must retain the above copyright notice, this list of conditions and the
//      following disclaimer.
//   2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the
//      following disclaimer in the documentation and/or other materials provided with the distribution.
//   3. Neither the name of the copyright holder nor the names of its contributors may be used to endorse or promote
//      products derived from this software without specific prior written permission.
//
//   THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,
//   INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
//   DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
//   SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
//   SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY,
//   WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE
//   USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

#pragma STDC FP_CONTRACT OFF

typedef unsigned int uint32_t;
typedef int int32_t;
typedef unsigned long long uint64_t;
typedef long long int64_t;
typedef unsigned int U32;

#define WINDOWS 1
#define ALM_PROTO_REF(fn) chipsharp_amd_##fn

#define SIGNBIT_DP64 0x8000000000000000ULL
#define MANTBITS_DP64 0x000fffffffffffffULL
#define EXPBITS_DP64 0x7ff0000000000000ULL
#define QNAN_MASK_64 0x0008000000000000ULL
#define PINFBITPATT_DP64 0x7ff0000000000000ULL
#define NINFBITPATT_DP64 0xfff0000000000000ULL
#define QNANBITPATT_DP64 0x7ff8000000000000ULL
#define INDEFBITPATT_DP64 0xfff8000000000000ULL
#define EXPBIAS_DP64 1023
#define EXPSHIFTBITS_DP64 52
#define BASEDIGITS_DP64 15
#define MANTLENGTH_DP64 53
#define EMIN_DP64 -1022
#define EMAX_DP64 1023
#define LAMBDA_DP64 1.0e300
#define POS_ZERO_F64 0x0000000000000000ULL
#define NEG_ZERO_F64 0x8000000000000000ULL

#define SIGNBIT_SP32 0x80000000U
#define MANTBITS_SP32 0x007fffffU
#define EXPBITS_SP32 0x7f800000U
#define QNAN_MASK_32 0x00400000U
#define PINFBITPATT_SP32 0x7f800000U
#define NINFBITPATT_SP32 0xff800000U
#define QNANBITPATT_SP32 0x7fc00000U
#define INDEFBITPATT_SP32 0xffc00000U
#define EXPBIAS_SP32 127
#define EXPSHIFTBITS_SP32 23
#define POS_ZERO_F32 0x00000000U
#define NEG_ZERO_F32 0x80000000U

#define asdouble(i) ((union { uint64_t _i; double _f; }){i})._f
#define asuint64(f) ((union { double _f; uint64_t _i; }){f})._i
#define asfloat(i) ((union { uint32_t _i; float _f; }){i})._f
#define asuint32(f) ((union { float _f; uint32_t _i; }){f})._i
#define GET_BITS_DP64(x, ux) ((ux) = asuint64(x))
#define PUT_BITS_DP64(ux, x) ((x) = asdouble(ux))
#define GET_BITS_SP32(x, ux) ((ux) = asuint32(x))
#define PUT_BITS_SP32(ux, x) ((x) = asfloat(ux))

// the flags of the special cases, which only raised exceptions and set errno
#define AMD_F_NONE 0
#define AMD_F_INEXACT 0
#define AMD_F_UNDERFLOW 0
#define AMD_F_OVERFLOW 0
#define AMD_F_INVALID 0
#define AMD_F_DIVBYZERO 0
static inline double __alm_handle_error(uint64_t bits, int flags) { (void)flags; return asdouble(bits); }
static inline float __alm_handle_errorf(uint32_t bits, int flags) { (void)flags; return asfloat(bits); }

// the SSE2 square root the Windows build uses, as ChipSharp's (chipsharp_float_math.c): the same correctly rounded root
double ChipSharp_Runtime_SqrtFloat64(double value);
float ChipSharp_Runtime_SqrtFloat32(float value);
#define _mm_setzero_pd() 0.0
#define _mm_load_sd(p) (*(p))
#define _mm_sqrt_sd(zero, x) ChipSharp_Runtime_SqrtFloat64(x)
#define _mm_store_sd(p, x) (*(p) = (x))
#define _mm_setzero_ps() 0.0f
#define _mm_load_ss(p) (*(p))
#define _mm_sqrt_ss(x) ChipSharp_Runtime_SqrtFloat32(x)
#define _mm_store_ss(p, x) (*(p) = (x))

// tanf's reduction of an argument of 500000 or more (ux: |x| as a double's bits), by musl's (chipsharp_math_musl.c)
int chipsharp_rem_pio2f(float x, double *y);
static void __amd_remainder_piby2d2f(uint64_t ux, double *r, int *region)
{
    double y;
    *region = chipsharp_rem_pio2f((float)asdouble(ux), &y) & 3;
    *r = y;
}

// ---- From libm_inlines_amd.h ------------------------------------------------------------------------------------

/* Scales the double x by 2.0**n.
   Assumes EMIN <= n <= EMAX, though this condition is not checked. */
static inline double scaleDouble_1(double x, int n) {
    /* Construct the number t = 2.0**n */
    return x * (asdouble(((uint64_t)n + EXPBIAS_DP64) << EXPSHIFTBITS_DP64));
}

/* Scales the double x by 2.0**n.
   Assumes 2*EMIN <= n <= 2*EMAX, though this condition is not checked. */
static inline double scaleDouble_2(double x, int n) {
    double t1, t2;
    int n1, n2;
    n1 = n / 2;
    n2 = n - n1;
    /* Construct the numbers t1 = 2.0**n1 and t2 = 2.0**n2 */
    t1 = asdouble(((uint64_t)n1 + EXPBIAS_DP64) << EXPSHIFTBITS_DP64);
    t2 = asdouble(((uint64_t)n2 + EXPBIAS_DP64) << EXPSHIFTBITS_DP64);

    return (x * t1) * t2;
}


/* Scales the float x by 2.0**n.
   Assumes 2*EMIN <= n <= 2*EMAX, though this condition is not checked. */
static inline float scaleFloat_2(float x, int n) {
    float t1, t2;
    int n1, n2;
    n1 = n / 2;
    n2 = n - n1;
    /* Construct the numbers t1 = 2.0**n1 and t2 = 2.0**n2 */
    t1 = asfloat(((uint32_t)n1 + EXPBIAS_SP32) << EXPSHIFTBITS_SP32);
    t2 = asfloat(((uint32_t)n2 + EXPBIAS_SP32) << EXPSHIFTBITS_SP32);

    return (x*t1)*t2;
}

/* Compute the values m, z1, and z2 such that base**x = 2**m * (z1 + z2).
   Small arguments abs(x) < 1/(16*ln(base)) and extreme arguments
   abs(x) > large/(ln(base)) (where large is the largest representable
   floating point number) should be handled separately instead of calling
   this function. This function is called by exp_amd, exp2_amd, exp10_amd,
   cosh_amd and sinh_amd. */
static inline void splitexp(double x, double logbase,
                            double thirtytwo_by_logbaseof2,
                            double logbaseof2_by_32_lead,
                            double logbaseof2_by_32_trail,
                            int *m, double *z1, double *z2)
{
    double q, r, r1, r2, f1, f2;
    int n, j;

  /* Arrays two_to_jby32_lead_table and two_to_jby32_trail_table contain
    leading and trailing parts respectively of precomputed
    values of pow(2.0,j/32.0), for j = 0, 1, ..., 31.
    two_to_jby32_lead_table contains the first 25 bits of precision,
    and two_to_jby32_trail_table contains a further 53 bits precision. */

    static const double two_to_jby32_lead_table[32] = {
        1.00000000000000000000e+00,   /* 0x3ff0000000000000 */
        1.02189713716506958008e+00,   /* 0x3ff059b0d0000000 */
        1.04427373409271240234e+00,   /* 0x3ff0b55860000000 */
        1.06714040040969848633e+00,   /* 0x3ff11301d0000000 */
        1.09050768613815307617e+00,   /* 0x3ff172b830000000 */
        1.11438673734664916992e+00,   /* 0x3ff1d48730000000 */
        1.13878858089447021484e+00,   /* 0x3ff2387a60000000 */
        1.16372483968734741211e+00,   /* 0x3ff29e9df0000000 */
        1.18920707702636718750e+00,   /* 0x3ff306fe00000000 */
        1.21524733304977416992e+00,   /* 0x3ff371a730000000 */
        1.24185776710510253906e+00,   /* 0x3ff3dea640000000 */
        1.26905095577239990234e+00,   /* 0x3ff44e0860000000 */
        1.29683953523635864258e+00,   /* 0x3ff4bfdad0000000 */
        1.32523661851882934570e+00,   /* 0x3ff5342b50000000 */
        1.35425549745559692383e+00,   /* 0x3ff5ab07d0000000 */
        1.38390988111495971680e+00,   /* 0x3ff6247eb0000000 */
        1.41421353816986083984e+00,   /* 0x3ff6a09e60000000 */
        1.44518077373504638672e+00,   /* 0x3ff71f75e0000000 */
        1.47682613134384155273e+00,   /* 0x3ff7a11470000000 */
        1.50916439294815063477e+00,   /* 0x3ff8258990000000 */
        1.54221081733703613281e+00,   /* 0x3ff8ace540000000 */
        1.57598084211349487305e+00,   /* 0x3ff93737b0000000 */
        1.61049032211303710938e+00,   /* 0x3ff9c49180000000 */
        1.64575546979904174805e+00,   /* 0x3ffa5503b0000000 */
        1.68179279565811157227e+00,   /* 0x3ffae89f90000000 */
        1.71861928701400756836e+00,   /* 0x3ffb7f76f0000000 */
        1.75625211000442504883e+00,   /* 0x3ffc199bd0000000 */
        1.79470902681350708008e+00,   /* 0x3ffcb720d0000000 */
        1.83400803804397583008e+00,   /* 0x3ffd5818d0000000 */
        1.87416762113571166992e+00,   /* 0x3ffdfc9730000000 */
        1.91520655155181884766e+00,   /* 0x3ffea4afa0000000 */
        1.95714408159255981445e+00};  /* 0x3fff507650000000 */

    static const double two_to_jby32_trail_table[32] = {
        0.00000000000000000000e+00,   /* 0x0000000000000000 */
        1.14890470981563546737e-08,   /* 0x3e48ac2ba1d73e2a */
        4.83347014379782142328e-08,   /* 0x3e69f3121ec53172 */
        2.67125131841396124714e-10,   /* 0x3df25b50a4ebbf1b */
        4.65271045830351350190e-08,   /* 0x3e68faa2f5b9bef9 */
        5.24924336638693782574e-09,   /* 0x3e368b9aa7805b80 */
        5.38622214388600821910e-08,   /* 0x3e6ceac470cd83f6 */
        1.90902301017041969782e-08,   /* 0x3e547f7b84b09745 */
        3.79763538792174980894e-08,   /* 0x3e64636e2a5bd1ab */
        2.69306947081946450986e-08,   /* 0x3e5ceaa72a9c5154 */
        4.49683815095311756138e-08,   /* 0x3e682468446b6824 */
        1.41933332021066904914e-09,   /* 0x3e18624b40c4dbd0 */
        1.94146510233556266402e-08,   /* 0x3e54d8a89c750e5e */
        2.46409119489264118569e-08,   /* 0x3e5a753e077c2a0f */
        4.94812958044698886494e-08,   /* 0x3e6a90a852b19260 */
        8.48872238075784476136e-10,   /* 0x3e0d2ac258f87d03 */
        2.42032342089579394887e-08,   /* 0x3e59fcef32422cbf */
        3.32420002333182569170e-08,   /* 0x3e61d8bee7ba46e2 */
        1.45956577586525322754e-08,   /* 0x3e4f580c36bea881 */
        3.46452721050003920866e-08,   /* 0x3e62999c25159f11 */
        8.07090469079979051284e-09,   /* 0x3e415506dadd3e2a */
        2.99439161340839520436e-09,   /* 0x3e29b8bc9e8a0388 */
        9.83621719880452147153e-09,   /* 0x3e451f8480e3e236 */
        8.35492309647188080486e-09,   /* 0x3e41f12ae45a1224 */
        3.48493175137966283582e-08,   /* 0x3e62b5a75abd0e6a */
        1.11084703472699692902e-08,   /* 0x3e47daf237553d84 */
        5.03688744342840346564e-08,   /* 0x3e6b0aa538444196 */
        4.81896001063495806249e-08,   /* 0x3e69df20d22a0798 */
        4.83653666334089557746e-08,   /* 0x3e69f7490e4bb40b */
        1.29745882314081237628e-08,   /* 0x3e4bdcdaf5cb4656 */
        9.84532844621636118964e-09,   /* 0x3e452486cc2c7b9d */
        4.25828404545651943883e-08};  /* 0x3e66dc8a80ce9f09 */

      /*
        Step 1. Reduce the argument.

        To perform argument reduction, we find the integer n such that
        x = n * logbaseof2/32 + remainder, |remainder| <= logbaseof2/64.
        n is defined by round-to-nearest-integer( x*32/logbaseof2 ) and
        remainder by x - n*logbaseof2/32. The calculation of n is
        straightforward whereas the computation of x - n*logbaseof2/32
        must be carried out carefully.
        logbaseof2/32 is so represented in two pieces that
        (1) logbaseof2/32 is known to extra precision, (2) the product
        of n and the leading piece is a model number and is hence
        calculated without error, and (3) the subtraction of the value
        obtained in (2) from x is a model number and is hence again
        obtained without error.
      */

    r = x * thirtytwo_by_logbaseof2;
    /* Set n = nearest integer to r */
    /* This is faster on Hammer */
    if (r > 0)
        n = (int)(r + 0.5);
    else
        n = (int)(r - 0.5);

    r1 = x - n * logbaseof2_by_32_lead;
    r2 =   - n * logbaseof2_by_32_trail;

    /* Set j = n mod 32:   5 mod 32 = 5,   -5 mod 32 = 27,  etc. */
    /* j = n % 32;
      if (j < 0) j += 32; */
    j = n & 0x0000001f;

    f1 = two_to_jby32_lead_table[j];
    f2 = two_to_jby32_trail_table[j];

    *m = (n - j) / 32;

    /* Step 2. The following is the core approximation. We approximate
      exp(r1+r2)-1 by a polynomial. */

    r1 *= logbase; r2 *= logbase;

    r = r1 + r2;
    q = r1 + (r2 +
              r*r*( 5.00000000000000008883e-01 +
                r*( 1.66666666665260878863e-01 +
                r*( 4.16666666662260795726e-02 +
                r*( 8.33336798434219616221e-03 +
                r*( 1.38889490863777199667e-03 ))))));

    /* Step 3. Function value reconstruction.
      We now reconstruct the exponential of the input argument
      so that exp(x) = 2**m * (z1 + z2).
      The order of the computation below must be strictly observed. */

    *z1 = f1;
    *z2 = f2 + ((f1 + f2) * q);
}

/* Compute the values m, z1, and z2 such that base**x = 2**m * (z1 + z2).
   Small arguments abs(x) < 1/(16*ln(base)) and extreme arguments
   abs(x) > large/(ln(base)) (where large is the largest representable
   floating point number) should be handled separately instead of calling
   this function. This function is called by exp_amd, exp2_amd, exp10_amd,
   cosh_amd and sinh_amd. */
static inline void splitexpf(float x, float logbase,
                             float thirtytwo_by_logbaseof2,
                             float logbaseof2_by_32_lead,
                             float logbaseof2_by_32_trail,
                             int *m, float *z1, float *z2)
{
    float q, r, r1, r2, f1, f2;
    int n, j;

  /* Arrays two_to_jby32_lead_table and two_to_jby32_trail_table contain
    leading and trailing parts respectively of precomputed
    values of pow(2.0,j/32.0), for j = 0, 1, ..., 31.
    two_to_jby32_lead_table contains the first 10 bits of precision,
    and two_to_jby32_trail_table contains a further 24 bits precision. */

    static const float two_to_jby32_lead_table[32] = {
        1.0000000000E+00F,  /* 0x3F800000 */
        1.0214843750E+00F,  /* 0x3F82C000 */
        1.0429687500E+00F,  /* 0x3F858000 */
        1.0664062500E+00F,  /* 0x3F888000 */
        1.0898437500E+00F,  /* 0x3F8B8000 */
        1.1132812500E+00F,  /* 0x3F8E8000 */
        1.1386718750E+00F,  /* 0x3F91C000 */
        1.1621093750E+00F,  /* 0x3F94C000 */
        1.1875000000E+00F,  /* 0x3F980000 */
        1.2148437500E+00F,  /* 0x3F9B8000 */
        1.2402343750E+00F,  /* 0x3F9EC000 */
        1.2675781250E+00F,  /* 0x3FA24000 */
        1.2949218750E+00F,  /* 0x3FA5C000 */
        1.3242187500E+00F,  /* 0x3FA98000 */
        1.3535156250E+00F,  /* 0x3FAD4000 */
        1.3828125000E+00F,  /* 0x3FB10000 */
        1.4140625000E+00F,  /* 0x3FB50000 */
        1.4433593750E+00F,  /* 0x3FB8C000 */
        1.4765625000E+00F,  /* 0x3FBD0000 */
        1.5078125000E+00F,  /* 0x3FC10000 */
        1.5410156250E+00F,  /* 0x3FC54000 */
        1.5742187500E+00F,  /* 0x3FC98000 */
        1.6093750000E+00F,  /* 0x3FCE0000 */
        1.6445312500E+00F,  /* 0x3FD28000 */
        1.6816406250E+00F,  /* 0x3FD74000 */
        1.7167968750E+00F,  /* 0x3FDBC000 */
        1.7558593750E+00F,  /* 0x3FE0C000 */
        1.7929687500E+00F,  /* 0x3FE58000 */
        1.8339843750E+00F,  /* 0x3FEAC000 */
        1.8730468750E+00F,  /* 0x3FEFC000 */
        1.9140625000E+00F,  /* 0x3FF50000 */
        1.9570312500E+00F}; /* 0x3FFA8000 */

    static const float two_to_jby32_trail_table[32] = {
        0.0000000000E+00F,  /* 0x00000000 */
        4.1277357377E-04F,  /* 0x39D86988 */
        1.3050324051E-03F,  /* 0x3AAB0D9F */
        7.3415064253E-04F,  /* 0x3A407404 */
        6.6398258787E-04F,  /* 0x3A2E0F1E */
        1.1054925853E-03F,  /* 0x3A90E62D */
        1.1675967835E-04F,  /* 0x38F4DCE0 */
        1.6154836630E-03F,  /* 0x3AD3BEA3 */
        1.7071149778E-03F,  /* 0x3ADFC146 */
        4.0360994171E-04F,  /* 0x39D39B9C */
        1.6234370414E-03F,  /* 0x3AD4C982 */
        1.4728321694E-03F,  /* 0x3AC10C0C */
        1.9176795613E-03F,  /* 0x3AFB5AA6 */
        1.0178930825E-03F,  /* 0x3A856AD3 */
        7.3992193211E-04F,  /* 0x3A41F752 */
        1.0973819299E-03F,  /* 0x3A8FD607 */
        1.5106226783E-04F,  /* 0x391E6678 */
        1.8214319134E-03F,  /* 0x3AEEBD1D */
        2.6364589576E-04F,  /* 0x398A39F4 */
        1.3519275235E-03F,  /* 0x3AB13329 */
        1.1952003697E-03F,  /* 0x3A9CA845 */
        1.7620950239E-03F,  /* 0x3AE6F619 */
        1.1153318919E-03F,  /* 0x3A923054 */
        1.2242280645E-03F,  /* 0x3AA07647 */
        1.5220546629E-04F,  /* 0x391F9958 */
        1.8224230735E-03F,  /* 0x3AEEDE5F */
        3.9278529584E-04F,  /* 0x39CDEEC0 */
        1.7403248930E-03F,  /* 0x3AE41B9D */
        2.3711356334E-05F,  /* 0x37C6E7C0 */
        1.1207590578E-03F,  /* 0x3A92E66F */
        1.1440613307E-03F,  /* 0x3A95F454 */
        1.1287408415E-04F}; /* 0x38ECB6D0 */

      /*
        Step 1. Reduce the argument.

        To perform argument reduction, we find the integer n such that
        x = n * logbaseof2/32 + remainder, |remainder| <= logbaseof2/64.
        n is defined by round-to-nearest-integer( x*32/logbaseof2 ) and
        remainder by x - n*logbaseof2/32. The calculation of n is
        straightforward whereas the computation of x - n*logbaseof2/32
        must be carried out carefully.
        logbaseof2/32 is so represented in two pieces that
        (1) logbaseof2/32 is known to extra precision, (2) the product
        of n and the leading piece is a model number and is hence
        calculated without error, and (3) the subtraction of the value
        obtained in (2) from x is a model number and is hence again
        obtained without error.
      */

    r = x * thirtytwo_by_logbaseof2;
    /* Set n = nearest integer to r */
    /* This is faster on Hammer */
    if (r > 0)
        n = (int)(r + 0.5F);
    else
        n = (int)(r - 0.5F);

    r1 = x - (float)n * logbaseof2_by_32_lead;
    r2 =   - (float)n * logbaseof2_by_32_trail;

    /* Set j = n mod 32:   5 mod 32 = 5,   -5 mod 32 = 27,  etc. */
    /* j = n % 32;
      if (j < 0) j += 32; */
    j = n & 0x0000001f;

    f1 = two_to_jby32_lead_table[j];
    f2 = two_to_jby32_trail_table[j];

    *m = (n - j) / 32;

    /* Step 2. The following is the core approximation. We approximate
      exp(r1+r2)-1 by a polynomial. */

    r1 *= logbase; r2 *= logbase;

    r = r1 + r2;
    q = r1 + (r2 +
              r*r*( 5.00000000000000008883e-01F +
                      r*( 1.66666666665260878863e-01F )));

    /* Step 3. Function value reconstruction.
      We now reconstruct the exponential of the input argument
      so that exp(x) = 2**m * (z1 + z2).
      The order of the computation below must be strictly observed. */

    *z1 = f1;
    *z2 = f2 + ((f1 + f2) * q);
}


/* Scales up a double (normal or denormal) whose bit pattern is given
   as ux by 2**1024. There are no checks that the input number is
   scalable by that amount. */
static inline void scaleUpDouble1024(unsigned long long ux, uint64_t *ur)
{
    unsigned long long uy;
    double y;

    if ((ux & EXPBITS_DP64) == 0){
        /* ux is denormalised */
        PUT_BITS_DP64(ux | 0x4010000000000000, y);
        if (ux & SIGNBIT_DP64)
            y += 4.0;
        else
            y -= 4.0;
        GET_BITS_DP64(y, uy);
    }
    else
        uy = ux + 0x4000000000000000; /* ux is normal */

    *ur = uy;
    return;
}



/* Scales down a double whose bit pattern is given as ux by 2**k.
   There are no checks that the input number is scalable by that amount. */
static inline void scaleDownDouble(unsigned long long ux, int k,
                                   uint64_t *ur)
{
    unsigned long long uy, uk, ax, xsign;
    int n, shift;
    xsign = ux & SIGNBIT_DP64;
    ax = ux & ~SIGNBIT_DP64;
    n = (int)((ax & EXPBITS_DP64) >> EXPSHIFTBITS_DP64) - k;
    if (n > 0){
        uk = (unsigned long long)n << EXPSHIFTBITS_DP64;
        uy = (ax & ~EXPBITS_DP64) | uk;
    }
    else{
        uy = (ax & ~EXPBITS_DP64) | 0x0010000000000000;
        shift = (1 - n);
        if (shift > MANTLENGTH_DP64 + 1)
            /* Sigh. Shifting works mod 64 so be careful not to shift too much */
            uy = 0;
        else{
            /* Make sure we round the result */
            uy >>= shift - 1;
            uy = (uy >> 1) + (uy & 1);
        }
    }
    *ur = uy | xsign;
}


/* ---- aocl-libm-ose src/ref/asin.c ---- */


double ALM_PROTO_REF(asin)(double x)
{
  /* Computes arcsin(x).
     The argument is first reduced by noting that arcsin(x)
     is invalid for abs(x) > 1 and arcsin(-x) = -arcsin(x).
     For denormal and small arguments arcsin(x) = x to machine
     accuracy. Remaining argument ranges are handled as follows.
     For abs(x) <= 0.5 use
     arcsin(x) = x + x^3*R(x^2)
     where R(x^2) is a rational minimax approximation to
     (arcsin(x) - x)/x^3.
     For abs(x) > 0.5 exploit the identity:
      arcsin(x) = pi/2 - 2*arcsin(sqrt(1-x)/2)
     together with the above rational approximation, and
     reconstruct the terms carefully.
    */

  /* Some constants and split constants. */

  static const double
    piby2_tail  = 6.1232339957367660e-17, /* 0x3c91a62633145c07 */
    hpiby2_head = 7.8539816339744831e-01, /* 0x3fe921fb54442d18 */
    piby2       = 1.5707963267948965e+00; /* 0x3ff921fb54442d18 */
  double u, v, y, s=0.0, r;
  int xexp, xnan, transform=0;

  unsigned long long ux, aux, xneg;
  GET_BITS_DP64(x, ux);
  aux = ux & ~SIGNBIT_DP64;
  xneg = (ux & SIGNBIT_DP64);
  xnan = (aux > PINFBITPATT_DP64);
  xexp = (int)((ux & EXPBITS_DP64) >> EXPSHIFTBITS_DP64) - EXPBIAS_DP64;

  /* Special cases */

  if (xnan)
    {
#ifdef WINDOWS
     return  __alm_handle_error(ux|0x0008000000000000, AMD_F_NONE);
#else
      //return x + x; /* With invalid if it's a signalling NaN */
      if (ux & QNAN_MASK_64)
     return  __alm_handle_error(ux|0x0008000000000000, AMD_F_NONE);
      else
     return  __alm_handle_error(ux|0x0008000000000000, AMD_F_INVALID);
#endif
    }
  else if (xexp < -28)
    { /* y small enough that arcsin(x) = x */
#ifdef WINDOWS
      return x; //val_with_flags(x, AMD_F_INEXACT);
#else
     if ((ux == SIGNBIT_DP64) || (ux == 0x0))
	return x;
     else
     return  __alm_handle_error(ux, AMD_F_UNDERFLOW | AMD_F_INEXACT);
#endif
    }
  else if (xexp >= 0)
    { /* abs(x) >= 1.0 */
      if (x == 1.0)
        return piby2; //val_with_flags(piby2, AMD_F_INEXACT);
      else if (x == -1.0)
        return -piby2;//val_with_flags(-piby2, AMD_F_INEXACT);
      else
#ifdef WINDOWS
     return  __alm_handle_error(INDEFBITPATT_DP64, AMD_F_INVALID);
#else
        //return retval_errno_edom(x);
     return  __alm_handle_error(INDEFBITPATT_DP64, AMD_F_INVALID);
#endif
    }

  if (xneg) y = -x;
  else y = x;

  transform = (xexp >= -1); /* abs(x) >= 0.5 */

  if (transform)
    { /* Transform y into the range [0,0.5) */
      r = 0.5*(1.0 - y);
#ifdef WINDOWS
      /* VC++ intrinsic call */
      _mm_store_sd(&s, _mm_sqrt_sd(_mm_setzero_pd(), _mm_load_sd(&r)));
#else
      /* Hammer sqrt instruction */
      asm volatile ("sqrtsd %1, %0" : "=x" (s) : "x" (r));
#endif
      y = s;
    }
  else
    r = y*y;

  /* Use a rational approximation for [0.0, 0.5] */

  u = r*(0.227485835556935010735943483075 +
         (-0.445017216867635649900123110649 +
          (0.275558175256937652532686256258 +
           (-0.0549989809235685841612020091328 +
            (0.00109242697235074662306043804220 +
             0.0000482901920344786991880522822991*r)*r)*r)*r)*r)/
    (1.36491501334161032038194214209 +
     (-3.28431505720958658909889444194 +
      (2.76568859157270989520376345954 +
       (-0.943639137032492685763471240072 +
        0.105869422087204370341222318533*r)*r)*r)*r);

  if (transform)
    { /* Reconstruct asin carefully in transformed region */
        {
          double c, s1, p, q;
          unsigned long long us;
          GET_BITS_DP64(s, us);
          PUT_BITS_DP64(0xffffffff00000000 & us, s1);
          c = (r-s1*s1)/(s+s1);
          p = 2.0*s*u - (piby2_tail-2.0*c);
          q = hpiby2_head - 2.0*s1;
          v = hpiby2_head - (p-q);
        }
    }
  else
    {
#ifdef WINDOWS
      /* Use a temporary variable to prevent VC++ rearranging
            y + y*u
         into
            y * (1 + u)
         and getting an incorrectly rounded result */
      double tmp;
      tmp = y * u;
      v = y + tmp;
#else
      v = y + y*u;
#endif
    }

  if (xneg) return -v;
  else return v;
}



/* ---- aocl-libm-ose src/ref/acos.c ---- */




double ALM_PROTO_REF(acos)(double x)
{
  /* Computes arccos(x).
     The argument is first reduced by noting that arccos(x)
     is invalid for abs(x) > 1. For denormal and small
     arguments arccos(x) = pi/2 to machine accuracy.
     Remaining argument ranges are handled as follows.
     For abs(x) <= 0.5 use
     arccos(x) = pi/2 - arcsin(x)
     = pi/2 - (x + x^3*R(x^2))
     where R(x^2) is a rational minimax approximation to
     (arcsin(x) - x)/x^3.
     For abs(x) > 0.5 exploit the identity:
     arccos(x) = pi - 2*arcsin(sqrt(1-x)/2)
     together with the above rational approximation, and
     reconstruct the terms carefully.
  */

  /* Some constants and split constants. */

  static const double
    pi         = 3.1415926535897933e+00, /* 0x400921fb54442d18 */
    piby2      = 1.5707963267948965580e+00, /* 0x3ff921fb54442d18 */
    piby2_head = 1.5707963267948965580e+00, /* 0x3ff921fb54442d18 */
    piby2_tail = 6.12323399573676603587e-17; /* 0x3c91a62633145c07 */

  double u, y, s=0.0, r;
  int xexp, xnan, transform=0;

  unsigned long long ux, aux, xneg;
  GET_BITS_DP64(x, ux);
  aux = ux & ~SIGNBIT_DP64;
  xneg = (ux & SIGNBIT_DP64);
  xnan = (aux > PINFBITPATT_DP64);
  xexp = (int)((ux & EXPBITS_DP64) >> EXPSHIFTBITS_DP64) - EXPBIAS_DP64;

  /* Special cases */

  if (xnan)
    {
#ifdef WINDOWS
     return  __alm_handle_error(ux|0x0008000000000000, AMD_F_NONE);
#else
      //return x + x; /* With invalid if it's a signalling NaN */
     if (ux & QNAN_MASK_64)
         return  __alm_handle_error(ux|0x0008000000000000, AMD_F_NONE);
     else
         return  __alm_handle_error(ux|0x0008000000000000, AMD_F_INVALID);
#endif
    }
  else if (xexp < -56)
    { /* y small enough that arccos(x) = pi/2 */
      return piby2;//val_with_flags(piby2, AMD_F_INEXACT);
    }
  else if (xexp >= 0)
    { /* abs(x) >= 1.0 */
      if (x == 1.0)
        return 0.0;
      else if (x == -1.0)
        return pi; //val_with_flags(pi, AMD_F_INEXACT);
      else
#ifdef WINDOWS
       return  __alm_handle_error(INDEFBITPATT_DP64, AMD_F_INVALID);
#else
        //return retval_errno_edom(x);
       return  __alm_handle_error(INDEFBITPATT_DP64, AMD_F_INVALID);
#endif
    }

  if (xneg) y = -x;
  else y = x;

  transform = (xexp >= -1); /* abs(x) >= 0.5 */

  if (transform)
    { /* Transform y into the range [0,0.5) */
      r = 0.5*(1.0 - y);
#ifdef WINDOWS
      /* VC++ intrinsic call */
      _mm_store_sd(&s, _mm_sqrt_sd(_mm_setzero_pd(), _mm_load_sd(&r)));
#else
      /* Hammer sqrt instruction */
      asm volatile ("sqrtsd %1, %0" : "=x" (s) : "x" (r));
#endif
      y = s;
    }
  else
    r = y*y;

  /* Use a rational approximation for [0.0, 0.5] */

  u = r*(0.227485835556935010735943483075 +
         (-0.445017216867635649900123110649 +
          (0.275558175256937652532686256258 +
           (-0.0549989809235685841612020091328 +
            (0.00109242697235074662306043804220 +
             0.0000482901920344786991880522822991*r)*r)*r)*r)*r)/
    (1.36491501334161032038194214209 +
     (-3.28431505720958658909889444194 +
      (2.76568859157270989520376345954 +
       (-0.943639137032492685763471240072 +
	0.105869422087204370341222318533*r)*r)*r)*r);

  if (transform)
    { /* Reconstruct acos carefully in transformed region */
      if (xneg) return pi - 2.0*(s+(y*u - piby2_tail));
      else
	{
	  double c, s1;
	  unsigned long long us;
	  GET_BITS_DP64(s, us);
	  PUT_BITS_DP64(0xffffffff00000000 & us, s1);
	  c = (r-s1*s1)/(s+s1);
          return 2.0*s1 + (2.0*c+2.0*y*u);
	}
    }
  else
    return piby2_head - (x - (piby2_tail - x*u));
}



/* ---- aocl-libm-ose src/ref/atan.c ---- */



double ALM_PROTO_REF(atan)(double x)
{

  /* Some constants and split constants. */

  static double piby2 = 1.5707963267948966e+00; /* 0x3ff921fb54442d18 */
  double chi, clo, v, s, q, z;

  /* Find properties of argument x. */

  unsigned long long ux, aux, xneg;
  GET_BITS_DP64(x, ux);
  aux = ux & ~SIGNBIT_DP64;
  xneg = (ux != aux);

  if (xneg) v = -x;
  else v = x;

  /* Argument reduction to range [-7/16,7/16] */

  if (aux < 0x3e50000000000000) /* v < 2.0^(-26) */
    {
      /* x is a good approximation to atan(x) and avoids working on
         intermediate denormal numbers */
      if (aux == 0x0000000000000000)
        return x;
      else
#ifdef WINDOWS
        return x ; //val_with_flags(x, AMD_F_INEXACT);
#else
	return  __alm_handle_error(ux, AMD_F_UNDERFLOW|AMD_F_INEXACT);
#endif
    }
  else if (aux > 0x4003800000000000) /* v > 39./16. */
    {

      if (aux > PINFBITPATT_DP64)
        {
          /* x is NaN */
#ifdef WINDOWS
		return  __alm_handle_error(ux|0x0008000000000000, AMD_F_NONE);
#else
          return x + x; /* Raise invalid if it's a signalling NaN */
#endif
        }
      else if (aux > 0x4370000000000000)
	{ /* abs(x) > 2^56 => arctan(1/x) is
	     insignificant compared to piby2 */
	  if (xneg)
            return -piby2;//val_with_flags(-piby2, AMD_F_INEXACT);
	  else
            return piby2;//val_with_flags(piby2, AMD_F_INEXACT);
	}

      x = -1.0/v;
      /* (chi + clo) = arctan(infinity) */
      chi = 1.57079632679489655800e+00; /* 0x3ff921fb54442d18 */
      clo = 6.12323399573676480327e-17; /* 0x3c91a62633145c06 */
    }
  else if (aux > 0x3ff3000000000000) /* 39./16. > v > 19./16. */
    {
      x = (v-1.5)/(1.0+1.5*v);
      /* (chi + clo) = arctan(1.5) */
      chi = 9.82793723247329054082e-01; /* 0x3fef730bd281f69b */
      clo = 1.39033110312309953701e-17; /* 0x3c7007887af0cbbc */
    }
  else if (aux > 0x3fe6000000000000) /* 19./16. > v > 11./16. */
    {
      x = (v-1.0)/(1.0+v);
      /* (chi + clo) = arctan(1.) */
      chi = 7.85398163397448278999e-01; /* 0x3fe921fb54442d18 */
      clo = 3.06161699786838240164e-17; /* 0x3c81a62633145c06 */
    }
  else if (aux > 0x3fdc000000000000) /* 11./16. > v > 7./16. */
    {
      x = (2.0*v-1.0)/(2.0+v);
      /* (chi + clo) = arctan(0.5) */
      chi = 4.63647609000806093515e-01; /* 0x3fddac670561bb4f */
      clo = 2.26987774529616809294e-17; /* 0x3c7a2b7f222f65e0 */
    }
  else  /* v < 7./16. */
    {
      x = v;
      chi = 0.0;
      clo = 0.0;
    }

  /* Core approximation: Remez(4,4) on [-7/16,7/16] */

  s = x*x;
  q = x*s*
       (0.268297920532545909e0 +
	(0.447677206805497472e0 +
	 (0.220638780716667420e0 +
	  (0.304455919504853031e-1 +
	    0.142316903342317766e-3*s)*s)*s)*s)/
       (0.804893761597637733e0 +
	(0.182596787737507063e1 +
	 (0.141254259931958921e1 +
	  (0.424602594203847109e0 +
	    0.389525873944742195e-1*s)*s)*s)*s);

  z = chi - ((q - clo) - x);

  if (xneg) z = -z;
  return z;
}



/* ---- aocl-libm-ose src/ref/atan2.c ---- */


double ALM_PROTO_REF(atan2)(double y, double x)
{
  /* Arrays atan_jby256_lead and atan_jby256_tail contain
     leading and trailing parts respectively of precomputed
     values of atan(j/256), for j = 16, 17, ..., 256.
     atan_jby256_lead contains the first 21 bits of precision,
     and atan_jby256_tail contains a further 53 bits precision. */

  static const double atan_jby256_lead[  241] = {
    6.24187886714935302734e-02,  /* 0x3faff55b00000000 */
    6.63088560104370117188e-02,  /* 0x3fb0f99e00000000 */
    7.01969265937805175781e-02,  /* 0x3fb1f86d00000000 */
    7.40829110145568847656e-02,  /* 0x3fb2f71900000000 */
    7.79666304588317871094e-02,  /* 0x3fb3f59f00000000 */
    8.18479657173156738281e-02,  /* 0x3fb4f3fd00000000 */
    8.57268571853637695312e-02,  /* 0x3fb5f23200000000 */
    8.96031260490417480469e-02,  /* 0x3fb6f03b00000000 */
    9.34767723083496093750e-02,  /* 0x3fb7ee1800000000 */
    9.73475575447082519531e-02,  /* 0x3fb8ebc500000000 */
    1.01215422153472900391e-01,  /* 0x3fb9e94100000000 */
    1.05080246925354003906e-01,  /* 0x3fbae68a00000000 */
    1.08941912651062011719e-01,  /* 0x3fbbe39e00000000 */
    1.12800359725952148438e-01,  /* 0x3fbce07c00000000 */
    1.16655409336090087891e-01,  /* 0x3fbddd2100000000 */
    1.20507001876831054688e-01,  /* 0x3fbed98c00000000 */
    1.24354958534240722656e-01,  /* 0x3fbfd5ba00000000 */
    1.28199219703674316406e-01,  /* 0x3fc068d500000000 */
    1.32039666175842285156e-01,  /* 0x3fc0e6ad00000000 */
    1.35876297950744628906e-01,  /* 0x3fc1646500000000 */
    1.39708757400512695312e-01,  /* 0x3fc1e1fa00000000 */
    1.43537282943725585938e-01,  /* 0x3fc25f6e00000000 */
    1.47361397743225097656e-01,  /* 0x3fc2dcbd00000000 */
    1.51181221008300781250e-01,  /* 0x3fc359e800000000 */
    1.54996633529663085938e-01,  /* 0x3fc3d6ee00000000 */
    1.58807516098022460938e-01,  /* 0x3fc453ce00000000 */
    1.62613749504089355469e-01,  /* 0x3fc4d08700000000 */
    1.66415214538574218750e-01,  /* 0x3fc54d1800000000 */
    1.70211911201477050781e-01,  /* 0x3fc5c98100000000 */
    1.74003481864929199219e-01,  /* 0x3fc645bf00000000 */
    1.77790164947509765625e-01,  /* 0x3fc6c1d400000000 */
    1.81571602821350097656e-01,  /* 0x3fc73dbd00000000 */
    1.85347914695739746094e-01,  /* 0x3fc7b97b00000000 */
    1.89118742942810058594e-01,  /* 0x3fc8350b00000000 */
    1.92884206771850585938e-01,  /* 0x3fc8b06e00000000 */
    1.96644186973571777344e-01,  /* 0x3fc92ba300000000 */
    2.00398445129394531250e-01,  /* 0x3fc9a6a800000000 */
    2.04147100448608398438e-01,  /* 0x3fca217e00000000 */
    2.07889914512634277344e-01,  /* 0x3fca9c2300000000 */
    2.11626768112182617188e-01,  /* 0x3fcb169600000000 */
    2.15357661247253417969e-01,  /* 0x3fcb90d700000000 */
    2.19082474708557128906e-01,  /* 0x3fcc0ae500000000 */
    2.22801089286804199219e-01,  /* 0x3fcc84bf00000000 */
    2.26513504981994628906e-01,  /* 0x3fccfe6500000000 */
    2.30219483375549316406e-01,  /* 0x3fcd77d500000000 */
    2.33919143676757812500e-01,  /* 0x3fcdf11000000000 */
    2.37612247467041015625e-01,  /* 0x3fce6a1400000000 */
    2.41298794746398925781e-01,  /* 0x3fcee2e100000000 */
    2.44978547096252441406e-01,  /* 0x3fcf5b7500000000 */
    2.48651623725891113281e-01,  /* 0x3fcfd3d100000000 */
    2.52317905426025390625e-01,  /* 0x3fd025fa00000000 */
    2.55977153778076171875e-01,  /* 0x3fd061ee00000000 */
    2.59629487991333007812e-01,  /* 0x3fd09dc500000000 */
    2.63274669647216796875e-01,  /* 0x3fd0d97e00000000 */
    2.66912937164306640625e-01,  /* 0x3fd1151a00000000 */
    2.70543813705444335938e-01,  /* 0x3fd1509700000000 */
    2.74167299270629882812e-01,  /* 0x3fd18bf500000000 */
    2.77783632278442382812e-01,  /* 0x3fd1c73500000000 */
    2.81392335891723632812e-01,  /* 0x3fd2025500000000 */
    2.84993648529052734375e-01,  /* 0x3fd23d5600000000 */
    2.88587331771850585938e-01,  /* 0x3fd2783700000000 */
    2.92173147201538085938e-01,  /* 0x3fd2b2f700000000 */
    2.95751571655273437500e-01,  /* 0x3fd2ed9800000000 */
    2.99322128295898437500e-01,  /* 0x3fd3281800000000 */
    3.02884817123413085938e-01,  /* 0x3fd3627700000000 */
    3.06439399719238281250e-01,  /* 0x3fd39cb400000000 */
    3.09986352920532226562e-01,  /* 0x3fd3d6d100000000 */
    3.13524961471557617188e-01,  /* 0x3fd410cb00000000 */
    3.17055702209472656250e-01,  /* 0x3fd44aa400000000 */
    3.20578098297119140625e-01,  /* 0x3fd4845a00000000 */
    3.24092388153076171875e-01,  /* 0x3fd4bdee00000000 */
    3.27598333358764648438e-01,  /* 0x3fd4f75f00000000 */
    3.31095933914184570312e-01,  /* 0x3fd530ad00000000 */
    3.34585189819335937500e-01,  /* 0x3fd569d800000000 */
    3.38066101074218750000e-01,  /* 0x3fd5a2e000000000 */
    3.41538190841674804688e-01,  /* 0x3fd5dbc300000000 */
    3.45002174377441406250e-01,  /* 0x3fd6148400000000 */
    3.48457098007202148438e-01,  /* 0x3fd64d1f00000000 */
    3.51903676986694335938e-01,  /* 0x3fd6859700000000 */
    3.55341434478759765625e-01,  /* 0x3fd6bdea00000000 */
    3.58770608901977539062e-01,  /* 0x3fd6f61900000000 */
    3.62190723419189453125e-01,  /* 0x3fd72e2200000000 */
    3.65602254867553710938e-01,  /* 0x3fd7660700000000 */
    3.69004726409912109375e-01,  /* 0x3fd79dc600000000 */
    3.72398376464843750000e-01,  /* 0x3fd7d56000000000 */
    3.75782966613769531250e-01,  /* 0x3fd80cd400000000 */
    3.79158496856689453125e-01,  /* 0x3fd8442200000000 */
    3.82525205612182617188e-01,  /* 0x3fd87b4b00000000 */
    3.85882616043090820312e-01,  /* 0x3fd8b24d00000000 */
    3.89230966567993164062e-01,  /* 0x3fd8e92900000000 */
    3.92570018768310546875e-01,  /* 0x3fd91fde00000000 */
    3.95900011062622070312e-01,  /* 0x3fd9566d00000000 */
    3.99220705032348632812e-01,  /* 0x3fd98cd500000000 */
    4.02532100677490234375e-01,  /* 0x3fd9c31600000000 */
    4.05834197998046875000e-01,  /* 0x3fd9f93000000000 */
    4.09126996994018554688e-01,  /* 0x3fda2f2300000000 */
    4.12410259246826171875e-01,  /* 0x3fda64ee00000000 */
    4.15684223175048828125e-01,  /* 0x3fda9a9200000000 */
    4.18948888778686523438e-01,  /* 0x3fdad00f00000000 */
    4.22204017639160156250e-01,  /* 0x3fdb056400000000 */
    4.25449609756469726562e-01,  /* 0x3fdb3a9100000000 */
    4.28685665130615234375e-01,  /* 0x3fdb6f9600000000 */
    4.31912183761596679688e-01,  /* 0x3fdba47300000000 */
    4.35129165649414062500e-01,  /* 0x3fdbd92800000000 */
    4.38336372375488281250e-01,  /* 0x3fdc0db400000000 */
    4.41534280776977539062e-01,  /* 0x3fdc421900000000 */
    4.44722414016723632812e-01,  /* 0x3fdc765500000000 */
    4.47900772094726562500e-01,  /* 0x3fdcaa6800000000 */
    4.51069593429565429688e-01,  /* 0x3fdcde5300000000 */
    4.54228639602661132812e-01,  /* 0x3fdd121500000000 */
    4.57377910614013671875e-01,  /* 0x3fdd45ae00000000 */
    4.60517644882202148438e-01,  /* 0x3fdd791f00000000 */
    4.63647603988647460938e-01,  /* 0x3fddac6700000000 */
    4.66767549514770507812e-01,  /* 0x3fdddf8500000000 */
    4.69877958297729492188e-01,  /* 0x3fde127b00000000 */
    4.72978591918945312500e-01,  /* 0x3fde454800000000 */
    4.76069211959838867188e-01,  /* 0x3fde77eb00000000 */
    4.79150056838989257812e-01,  /* 0x3fdeaa6500000000 */
    4.82221126556396484375e-01,  /* 0x3fdedcb600000000 */
    4.85282421112060546875e-01,  /* 0x3fdf0ede00000000 */
    4.88333940505981445312e-01,  /* 0x3fdf40dd00000000 */
    4.91375446319580078125e-01,  /* 0x3fdf72b200000000 */
    4.94406938552856445312e-01,  /* 0x3fdfa45d00000000 */
    4.97428894042968750000e-01,  /* 0x3fdfd5e000000000 */
    5.00440597534179687500e-01,  /* 0x3fe0039c00000000 */
    5.03442764282226562500e-01,  /* 0x3fe01c3400000000 */
    5.06434917449951171875e-01,  /* 0x3fe034b700000000 */
    5.09417057037353515625e-01,  /* 0x3fe04d2500000000 */
    5.12389183044433593750e-01,  /* 0x3fe0657e00000000 */
    5.15351772308349609375e-01,  /* 0x3fe07dc300000000 */
    5.18304347991943359375e-01,  /* 0x3fe095f300000000 */
    5.21246910095214843750e-01,  /* 0x3fe0ae0e00000000 */
    5.24179458618164062500e-01,  /* 0x3fe0c61400000000 */
    5.27101993560791015625e-01,  /* 0x3fe0de0500000000 */
    5.30014991760253906250e-01,  /* 0x3fe0f5e200000000 */
    5.32917976379394531250e-01,  /* 0x3fe10daa00000000 */
    5.35810947418212890625e-01,  /* 0x3fe1255d00000000 */
    5.38693904876708984375e-01,  /* 0x3fe13cfb00000000 */
    5.41567325592041015625e-01,  /* 0x3fe1548500000000 */
    5.44430732727050781250e-01,  /* 0x3fe16bfa00000000 */
    5.47284126281738281250e-01,  /* 0x3fe1835a00000000 */
    5.50127506256103515625e-01,  /* 0x3fe19aa500000000 */
    5.52961349487304687500e-01,  /* 0x3fe1b1dc00000000 */
    5.55785179138183593750e-01,  /* 0x3fe1c8fe00000000 */
    5.58598995208740234375e-01,  /* 0x3fe1e00b00000000 */
    5.61403274536132812500e-01,  /* 0x3fe1f70400000000 */
    5.64197540283203125000e-01,  /* 0x3fe20de800000000 */
    5.66981792449951171875e-01,  /* 0x3fe224b700000000 */
    5.69756031036376953125e-01,  /* 0x3fe23b7100000000 */
    5.72520732879638671875e-01,  /* 0x3fe2521700000000 */
    5.75275897979736328125e-01,  /* 0x3fe268a900000000 */
    5.78021049499511718750e-01,  /* 0x3fe27f2600000000 */
    5.80756187438964843750e-01,  /* 0x3fe2958e00000000 */
    5.83481788635253906250e-01,  /* 0x3fe2abe200000000 */
    5.86197376251220703125e-01,  /* 0x3fe2c22100000000 */
    5.88903427124023437500e-01,  /* 0x3fe2d84c00000000 */
    5.91599464416503906250e-01,  /* 0x3fe2ee6200000000 */
    5.94285964965820312500e-01,  /* 0x3fe3046400000000 */
    5.96962928771972656250e-01,  /* 0x3fe31a5200000000 */
    5.99629878997802734375e-01,  /* 0x3fe3302b00000000 */
    6.02287292480468750000e-01,  /* 0x3fe345f000000000 */
    6.04934692382812500000e-01,  /* 0x3fe35ba000000000 */
    6.07573032379150390625e-01,  /* 0x3fe3713d00000000 */
    6.10201358795166015625e-01,  /* 0x3fe386c500000000 */
    6.12820148468017578125e-01,  /* 0x3fe39c3900000000 */
    6.15428924560546875000e-01,  /* 0x3fe3b19800000000 */
    6.18028640747070312500e-01,  /* 0x3fe3c6e400000000 */
    6.20618820190429687500e-01,  /* 0x3fe3dc1c00000000 */
    6.23198986053466796875e-01,  /* 0x3fe3f13f00000000 */
    6.25770092010498046875e-01,  /* 0x3fe4064f00000000 */
    6.28331184387207031250e-01,  /* 0x3fe41b4a00000000 */
    6.30883216857910156250e-01,  /* 0x3fe4303200000000 */
    6.33425712585449218750e-01,  /* 0x3fe4450600000000 */
    6.35958671569824218750e-01,  /* 0x3fe459c600000000 */
    6.38482093811035156250e-01,  /* 0x3fe46e7200000000 */
    6.40995979309082031250e-01,  /* 0x3fe4830a00000000 */
    6.43500804901123046875e-01,  /* 0x3fe4978f00000000 */
    6.45996093750000000000e-01,  /* 0x3fe4ac0000000000 */
    6.48482322692871093750e-01,  /* 0x3fe4c05e00000000 */
    6.50959014892578125000e-01,  /* 0x3fe4d4a800000000 */
    6.53426170349121093750e-01,  /* 0x3fe4e8de00000000 */
    6.55884265899658203125e-01,  /* 0x3fe4fd0100000000 */
    6.58332824707031250000e-01,  /* 0x3fe5111000000000 */
    6.60772323608398437500e-01,  /* 0x3fe5250c00000000 */
    6.63202762603759765625e-01,  /* 0x3fe538f500000000 */
    6.65623664855957031250e-01,  /* 0x3fe54cca00000000 */
    6.68035984039306640625e-01,  /* 0x3fe5608d00000000 */
    6.70438766479492187500e-01,  /* 0x3fe5743c00000000 */
    6.72832489013671875000e-01,  /* 0x3fe587d800000000 */
    6.75216674804687500000e-01,  /* 0x3fe59b6000000000 */
    6.77592277526855468750e-01,  /* 0x3fe5aed600000000 */
    6.79958820343017578125e-01,  /* 0x3fe5c23900000000 */
    6.82316303253173828125e-01,  /* 0x3fe5d58900000000 */
    6.84664726257324218750e-01,  /* 0x3fe5e8c600000000 */
    6.87004089355468750000e-01,  /* 0x3fe5fbf000000000 */
    6.89334869384765625000e-01,  /* 0x3fe60f0800000000 */
    6.91656589508056640625e-01,  /* 0x3fe6220d00000000 */
    6.93969249725341796875e-01,  /* 0x3fe634ff00000000 */
    6.96272850036621093750e-01,  /* 0x3fe647de00000000 */
    6.98567867279052734375e-01,  /* 0x3fe65aab00000000 */
    7.00854301452636718750e-01,  /* 0x3fe66d6600000000 */
    7.03131675720214843750e-01,  /* 0x3fe6800e00000000 */
    7.05400466918945312500e-01,  /* 0x3fe692a400000000 */
    7.07660198211669921875e-01,  /* 0x3fe6a52700000000 */
    7.09911346435546875000e-01,  /* 0x3fe6b79800000000 */
    7.12153911590576171875e-01,  /* 0x3fe6c9f700000000 */
    7.14387893676757812500e-01,  /* 0x3fe6dc4400000000 */
    7.16613292694091796875e-01,  /* 0x3fe6ee7f00000000 */
    7.18829631805419921875e-01,  /* 0x3fe700a700000000 */
    7.21037864685058593750e-01,  /* 0x3fe712be00000000 */
    7.23237514495849609375e-01,  /* 0x3fe724c300000000 */
    7.25428581237792968750e-01,  /* 0x3fe736b600000000 */
    7.27611064910888671875e-01,  /* 0x3fe7489700000000 */
    7.29785442352294921875e-01,  /* 0x3fe75a6700000000 */
    7.31950759887695312500e-01,  /* 0x3fe76c2400000000 */
    7.34108448028564453125e-01,  /* 0x3fe77dd100000000 */
    7.36257076263427734375e-01,  /* 0x3fe78f6b00000000 */
    7.38397598266601562500e-01,  /* 0x3fe7a0f400000000 */
    7.40530014038085937500e-01,  /* 0x3fe7b26c00000000 */
    7.42654323577880859375e-01,  /* 0x3fe7c3d300000000 */
    7.44770050048828125000e-01,  /* 0x3fe7d52800000000 */
    7.46877670288085937500e-01,  /* 0x3fe7e66c00000000 */
    7.48976707458496093750e-01,  /* 0x3fe7f79e00000000 */
    7.51068115234375000000e-01,  /* 0x3fe808c000000000 */
    7.53150939941406250000e-01,  /* 0x3fe819d000000000 */
    7.55226135253906250000e-01,  /* 0x3fe82ad000000000 */
    7.57292747497558593750e-01,  /* 0x3fe83bbe00000000 */
    7.59351730346679687500e-01,  /* 0x3fe84c9c00000000 */
    7.61402606964111328125e-01,  /* 0x3fe85d6900000000 */
    7.63445377349853515625e-01,  /* 0x3fe86e2500000000 */
    7.65480041503906250000e-01,  /* 0x3fe87ed000000000 */
    7.67507076263427734375e-01,  /* 0x3fe88f6b00000000 */
    7.69526004791259765625e-01,  /* 0x3fe89ff500000000 */
    7.71537303924560546875e-01,  /* 0x3fe8b06f00000000 */
    7.73540973663330078125e-01,  /* 0x3fe8c0d900000000 */
    7.75536537170410156250e-01,  /* 0x3fe8d13200000000 */
    7.77523994445800781250e-01,  /* 0x3fe8e17a00000000 */
    7.79504299163818359375e-01,  /* 0x3fe8f1b300000000 */
    7.81476497650146484375e-01,  /* 0x3fe901db00000000 */
    7.83441066741943359375e-01,  /* 0x3fe911f300000000 */
    7.85398006439208984375e-01}; /* 0x3fe921fb00000000 */

  static const double atan_jby256_tail[  241] = {
    2.13244638182005395671e-08,  /* 0x3e56e59fbd38db2c */
    3.89093864761712760656e-08,  /* 0x3e64e3aa54dedf96 */
    4.44780900009437454576e-08,  /* 0x3e67e105ab1bda88 */
    1.15344768460112754160e-08,  /* 0x3e48c5254d013fd0 */
    3.37271051945395312705e-09,  /* 0x3e2cf8ab3ad62670 */
    2.40857608736109859459e-08,  /* 0x3e59dca4bec80468 */
    1.85853810450623807768e-08,  /* 0x3e53f4b5ec98a8da */
    5.14358299969225078306e-08,  /* 0x3e6b9d49619d81fe */
    8.85023985412952486748e-09,  /* 0x3e43017887460934 */
    1.59425154214358432060e-08,  /* 0x3e511e3eca0b9944 */
    1.95139937737755753164e-08,  /* 0x3e54f3f73c5a332e */
    2.64909755273544319715e-08,  /* 0x3e5c71c8ae0e00a6 */
    4.43388037881231070144e-08,  /* 0x3e67cde0f86fbdc7 */
    2.14757072421821274557e-08,  /* 0x3e570f328c889c72 */
    2.61049792670754218852e-08,  /* 0x3e5c07ae9b994efe */
    7.81439350674466302231e-09,  /* 0x3e40c8021d7b1698 */
    3.60125207123751024094e-08,  /* 0x3e635585edb8cb22 */
    6.15276238179343767917e-08,  /* 0x3e70842567b30e96 */
    9.54387964641184285058e-08,  /* 0x3e799e811031472e */
    3.02789566851502754129e-08,  /* 0x3e6041821416bcee */
    1.16888650949870856331e-07,  /* 0x3e7f6086e4dc96f4 */
    1.07580956468653338863e-08,  /* 0x3e471a535c5f1b58 */
    8.33454265379535427653e-08,  /* 0x3e765f743fe63ca1 */
    1.10790279272629526068e-07,  /* 0x3e7dbd733472d014 */
    1.08394277896366207424e-07,  /* 0x3e7d18cc4d8b0d1d */
    9.22176086126841098800e-08,  /* 0x3e78c12553c8fb29 */
    7.90938592199048786990e-08,  /* 0x3e753b49e2e8f991 */
    8.66445407164293125637e-08,  /* 0x3e77422ae148c141 */
    1.40839973537092438671e-08,  /* 0x3e4e3ec269df56a8 */
    1.19070438507307600689e-07,  /* 0x3e7ff6754e7e0ac9 */
    6.40451663051716197071e-08,  /* 0x3e7131267b1b5aad */
    1.08338682076343674522e-07,  /* 0x3e7d14fa403a94bc */
    3.52999550187922736222e-08,  /* 0x3e62f396c089a3d8 */
    1.05983273930043077202e-07,  /* 0x3e7c731d78fa95bb */
    1.05486124078259553339e-07,  /* 0x3e7c50f385177399 */
    5.82167732281776477773e-08,  /* 0x3e6f41409c6f2c20 */
    1.08696483983403942633e-07,  /* 0x3e7d2d90c4c39ec0 */
    4.47335086122377542835e-08,  /* 0x3e680420696f2106 */
    1.26896287162615723528e-08,  /* 0x3e4b40327943a2e8 */
    4.06534471589151404531e-08,  /* 0x3e65d35e02f3d2a2 */
    3.84504846300557026690e-08,  /* 0x3e64a498288117b0 */
    3.60715006404807269080e-08,  /* 0x3e635da119afb324 */
    6.44725903165522722801e-08,  /* 0x3e714e85cdb9a908 */
    3.63749249976409461305e-08,  /* 0x3e638754e5547b9a */
    1.03901294413833913794e-07,  /* 0x3e7be40ae6ce3246 */
    6.25379756302167880580e-08,  /* 0x3e70c993b3bea7e7 */
    6.63984302368488828029e-08,  /* 0x3e71d2dd89ac3359 */
    3.21844598971548278059e-08,  /* 0x3e61476603332c46 */
    1.16030611712765830905e-07,  /* 0x3e7f25901bac55b7 */
    1.17464622142347730134e-07,  /* 0x3e7f881b7c826e28 */
    7.54604017965808996596e-08,  /* 0x3e7441996d698d20 */
    1.49234929356206556899e-07,  /* 0x3e8407ac521ea089 */
    1.41416924523217430259e-07,  /* 0x3e82fb0c6c4b1723 */
    2.13308065617483489011e-07,  /* 0x3e8ca135966a3e18 */
    5.04230937933302320146e-08,  /* 0x3e6b1218e4d646e4 */
    5.45874922281655519035e-08,  /* 0x3e6d4e72a350d288 */
    1.51849028914786868886e-07,  /* 0x3e84617e2f04c329 */
    3.09004308703769273010e-08,  /* 0x3e6096ec41e82650 */
    9.67574548184738317664e-08,  /* 0x3e79f91f25773e6e */
    4.02508285529322212824e-08,  /* 0x3e659c0820f1d674 */
    3.01222268096861091157e-08,  /* 0x3e602bf7a2df1064 */
    2.36189860670079288680e-07,  /* 0x3e8fb36bfc40508f */
    1.14095158111080887695e-07,  /* 0x3e7ea08f3f8dc892 */
    7.42349089746573467487e-08,  /* 0x3e73ed6254656a0e */
    5.12515583196230380184e-08,  /* 0x3e6b83f5e5e69c58 */
    2.19290391828763918102e-07,  /* 0x3e8d6ec2af768592 */
    3.83263512187553886471e-08,  /* 0x3e6493889a226f94 */
    1.61513486284090523855e-07,  /* 0x3e85ad8fa65279ba */
    5.09996743535589922261e-08,  /* 0x3e6b615784d45434 */
    1.23694037861246766534e-07,  /* 0x3e809a184368f145 */
    8.23367955351123783984e-08,  /* 0x3e761a2439b0d91c */
    1.07591766213053694014e-07,  /* 0x3e7ce1a65e39a978 */
    1.42789947524631815640e-07,  /* 0x3e832a39a93b6a66 */
    1.32347123024711878538e-07,  /* 0x3e81c3699af804e7 */
    2.17626067316598149229e-08,  /* 0x3e575e0f4e44ede8 */
    2.34454866923044288656e-07,  /* 0x3e8f77ced1a7a83b */
    2.82966370261766916053e-09,  /* 0x3e284e7f0cb1b500 */
    2.29300919890907632975e-07,  /* 0x3e8ec6b838b02dfe */
    1.48428270450261284915e-07,  /* 0x3e83ebf4dfbeda87 */
    1.87937408574313982512e-07,  /* 0x3e89397aed9cb475 */
    6.13685946813334055347e-08,  /* 0x3e707937bc239c54 */
    1.98585022733583817493e-07,  /* 0x3e8aa754553131b6 */
    7.68394131623752961662e-08,  /* 0x3e74a05d407c45dc */
    1.28119052312436745644e-07,  /* 0x3e8132231a206dd0 */
    7.02119104719236502733e-08,  /* 0x3e72d8ecfdd69c88 */
    9.87954793820636301943e-08,  /* 0x3e7a852c74218606 */
    1.72176752381034986217e-07,  /* 0x3e871bf2baeebb50 */
    1.12877225146169704119e-08,  /* 0x3e483d7db7491820 */
    5.33549829555851737993e-08,  /* 0x3e6ca50d92b6da14 */
    2.13833275710816521345e-08,  /* 0x3e56f5cde8530298 */
    1.16243518048290556393e-07,  /* 0x3e7f343198910740 */
    6.29926408369055877943e-08,  /* 0x3e70e8d241ccd80a */
    6.45429039328021963791e-08,  /* 0x3e71535ac619e6c8 */
    8.64001922814281933403e-08,  /* 0x3e77316041c36cd2 */
    9.50767572202325800240e-08,  /* 0x3e7985a000637d8e */
    5.80851497508121135975e-08,  /* 0x3e6f2f29858c0a68 */
    1.82350561135024766232e-07,  /* 0x3e8879847f96d909 */
    1.98948680587390608655e-07,  /* 0x3e8ab3d319e12e42 */
    7.83548663450197659846e-08,  /* 0x3e75088162dfc4c2 */
    3.04374234486798594427e-08,  /* 0x3e605749a1cd9d8c */
    2.76135725629797411787e-08,  /* 0x3e5da65c6c6b8618 */
    4.32610105454203065470e-08,  /* 0x3e6739bf7df1ad64 */
    5.17107515324127256994e-08,  /* 0x3e6bc31252aa3340 */
    2.82398327875841444660e-08,  /* 0x3e5e528191ad3aa8 */
    1.87482469524195595399e-07,  /* 0x3e8929d93df19f18 */
    2.97481891662714096139e-08,  /* 0x3e5ff11eb693a080 */
    9.94421570843584316402e-09,  /* 0x3e455ae3f145a3a0 */
    1.07056210730391848428e-07,  /* 0x3e7cbcd8c6c0ca82 */
    6.25589580466881163081e-08,  /* 0x3e70cb04d425d304 */
    9.56641013869464593803e-08,  /* 0x3e79adfcab5be678 */
    1.88056307148355440276e-07,  /* 0x3e893d90c5662508 */
    8.38850689379557880950e-08,  /* 0x3e768489bd35ff40 */
    5.01215865527674122924e-09,  /* 0x3e3586ed3da2b7e0 */
    1.74166095998522089762e-07,  /* 0x3e87604d2e850eee */
    9.96779574395363585849e-08,  /* 0x3e7ac1d12bfb53d8 */
    5.98432026368321460686e-09,  /* 0x3e39b3d468274740 */
    1.18362922366887577169e-07,  /* 0x3e7fc5d68d10e53c */
    1.86086833284154215946e-07,  /* 0x3e88f9e51884becb */
    1.97671457251348941011e-07,  /* 0x3e8a87f0869c06d1 */
    1.42447160717199237159e-07,  /* 0x3e831e7279f685fa */
    1.05504240785546574184e-08,  /* 0x3e46a8282f9719b0 */
    3.13335218371639189324e-08,  /* 0x3e60d2724a8a44e0 */
    1.96518418901914535399e-07,  /* 0x3e8a60524b11ad4e */
    2.17692035039173536059e-08,  /* 0x3e575fdf832750f0 */
    2.15613114426529981675e-07,  /* 0x3e8cf06902e4cd36 */
    5.68271098300441214948e-08,  /* 0x3e6e82422d4f6d10 */
    1.70331455823369124256e-08,  /* 0x3e524a091063e6c0 */
    9.17590028095709583247e-08,  /* 0x3e78a1a172dc6f38 */
    2.77266304112916566247e-07,  /* 0x3e929b6619f8a92d */
    9.37041937614656939690e-08,  /* 0x3e79274d9c1b70c8 */
    1.56116346368316796511e-08,  /* 0x3e50c34b1fbb7930 */
    4.13967433808382727413e-08,  /* 0x3e6639866c20eb50 */
    1.70164749185821616276e-07,  /* 0x3e86d6d0f6832e9e */
    4.01708788545600086008e-07,  /* 0x3e9af54def99f25e */
    2.59663539226050551563e-07,  /* 0x3e916cfc52a00262 */
    2.22007487655027469542e-07,  /* 0x3e8dcc1e83569c32 */
    2.90542250809644081369e-07,  /* 0x3e937f7a551ed425 */
    4.67720537666628903341e-07,  /* 0x3e9f6360adc98887 */
    2.79799803956772554802e-07,  /* 0x3e92c6ec8d35a2c1 */
    2.07344552327432547723e-07,  /* 0x3e8bd44df84cb036 */
    2.54705698692735196368e-07,  /* 0x3e9117cf826e310e */
    4.26848589539548450728e-07,  /* 0x3e9ca533f332cfc9 */
    2.52506723633552216197e-07,  /* 0x3e90f208509dbc2e */
    2.14684129933849704964e-07,  /* 0x3e8cd07d93c945de */
    3.20134822201596505431e-07,  /* 0x3e957bdfd67e6d72 */
    9.93537565749855712134e-08,  /* 0x3e7aab89c516c658 */
    3.70792944827917252327e-08,  /* 0x3e63e823b1a1b8a0 */
    1.41772749369083698972e-07,  /* 0x3e8307464a9d6d3c */
    4.22446601490198804306e-07,  /* 0x3e9c5993cd438843 */
    4.11818433724801511540e-07,  /* 0x3e9ba2fca02ab554 */
    1.19976381502605310519e-07,  /* 0x3e801a5b6983a268 */
    3.43703078571520905265e-08,  /* 0x3e6273d1b350efc8 */
    1.66128705555453270379e-07,  /* 0x3e864c238c37b0c6 */
    5.00499610023283006540e-08,  /* 0x3e6aded07370a300 */
    1.75105139941208062123e-07,  /* 0x3e878091197eb47e */
    7.70807146729030327334e-08,  /* 0x3e74b0f245e0dabc */
    2.45918607526895836121e-07,  /* 0x3e9080d9794e2eaf */
    2.18359020958626199345e-07,  /* 0x3e8d4ec242b60c76 */
    8.44342887976445333569e-09,  /* 0x3e4221d2f940caa0 */
    1.07506148687888629299e-07,  /* 0x3e7cdbc42b2bba5c */
    5.36544954316820904572e-08,  /* 0x3e6cce37bb440840 */
    3.39109101518396596341e-07,  /* 0x3e96c1d999cf1dd0 */
    2.60098720293920613340e-08,  /* 0x3e5bed8a07eb0870 */
    8.42678991664621455827e-08,  /* 0x3e769ed88f490e3c */
    5.36972237470183633197e-08,  /* 0x3e6cd41719b73ef0 */
    4.28192558171921681288e-07,  /* 0x3e9cbc4ac95b41b7 */
    2.71535491483955143294e-07,  /* 0x3e9238f1b890f5d7 */
    7.84094998145075780203e-08,  /* 0x3e750c4282259cc4 */
    3.43880599134117431863e-07,  /* 0x3e9713d2de87b3e2 */
    1.32878065060366481043e-07,  /* 0x3e81d5a7d2255276 */
    4.18046802627967629428e-07,  /* 0x3e9c0dfd48227ac1 */
    2.65042411765766019424e-07,  /* 0x3e91c964dab76753 */
    1.70383695347518643694e-07,  /* 0x3e86de56d5704496 */
    1.54096497259613515678e-07,  /* 0x3e84aeb71fd19968 */
    2.36543402412459813461e-07,  /* 0x3e8fbf91c57b1918 */
    4.38416350106876736790e-07,  /* 0x3e9d6bef7fbe5d9a */
    3.03892161339927775731e-07,  /* 0x3e9464d3dc249066 */
    3.31136771605664899240e-07,  /* 0x3e9638e2ec4d9073 */
    6.49494294526590682218e-08,  /* 0x3e716f4a7247ea7c */
    4.10423429887181345747e-09,  /* 0x3e31a0a740f1d440 */
    1.70831640869113847224e-07,  /* 0x3e86edbb0114a33c */
    1.10811512657909180966e-07,  /* 0x3e7dbee8bf1d513c */
    3.23677724749783611964e-07,  /* 0x3e95b8bdb0248f73 */
    3.55662734259192678528e-07,  /* 0x3e97de3d3f5eac64 */
    2.30102333489738219140e-07,  /* 0x3e8ee24187ae448a */
    4.47429004000738629714e-07,  /* 0x3e9e06c591ec5192 */
    7.78167135617329598659e-08,  /* 0x3e74e3861a332738 */
    9.90345291908535415737e-08,  /* 0x3e7a9599dcc2bfe4 */
    5.85800913143113728314e-08,  /* 0x3e6f732fbad43468 */
    4.57859062410871843857e-07,  /* 0x3e9eb9f573b727d9 */
    3.67993069723390929794e-07,  /* 0x3e98b212a2eb9897 */
    2.90836464322977276043e-07,  /* 0x3e9384884c167215 */
    2.51621574250131388318e-07,  /* 0x3e90e2d363020051 */
    2.75789824740652815545e-07,  /* 0x3e92820879fbd022 */
    3.88985776250314403593e-07,  /* 0x3e9a1ab9893e4b30 */
    1.40214080183768019611e-07,  /* 0x3e82d1b817a24478 */
    3.23451432223550478373e-08,  /* 0x3e615d7b8ded4878 */
    9.15979180730608444470e-08,  /* 0x3e78968f9db3a5e4 */
    3.44371402498640470421e-07,  /* 0x3e971c4171fe135f */
    3.40401897215059498077e-07,  /* 0x3e96d80f605d0d8c */
    1.06431813453707950243e-07,  /* 0x3e7c91f043691590 */
    1.46204238932338846248e-07,  /* 0x3e839f8a15fce2b2 */
    9.94610376972039046878e-09,  /* 0x3e455beda9d94b80 */
    2.01711528092681771039e-07,  /* 0x3e8b12c15d60949a */
    2.72027977986191568296e-07,  /* 0x3e924167b312bfe3 */
    2.48402602511693757964e-07,  /* 0x3e90ab8633070277 */
    1.58480011219249621715e-07,  /* 0x3e854554ebbc80ee */
    3.00372828113368713281e-08,  /* 0x3e60204aef5a4bb8 */
    3.67816204583541976394e-07,  /* 0x3e98af08c679cf2c */
    2.46169793032343824291e-07,  /* 0x3e90852a330ae6c8 */
    1.70080468270204253247e-07,  /* 0x3e86d3eb9ec32916 */
    1.67806717763872914315e-07,  /* 0x3e8685cb7fcbbafe */
    2.67715622006907942620e-07,  /* 0x3e91f751c1e0bd95 */
    2.14411342550299170574e-08,  /* 0x3e5705b1b0f72560 */
    4.11228221283669073277e-07,  /* 0x3e9b98d8d808ca92 */
    3.52311752396749662260e-08,  /* 0x3e62ea22c75cc980 */
    3.52718000397367821054e-07,  /* 0x3e97aba62bca0350 */
    4.38857387992911129814e-07,  /* 0x3e9d73833442278c */
    3.22574606753482540743e-07,  /* 0x3e95a5ca1fb18bf9 */
    3.28730371182804296828e-08,  /* 0x3e61a6092b6ecf28 */
    7.56672470607639279700e-08,  /* 0x3e744fd049aac104 */
    3.26750155316369681821e-09,  /* 0x3e2c114fd8df5180 */
    3.21724445362095284743e-07,  /* 0x3e95972f130feae5 */
    1.06639427371776571151e-07,  /* 0x3e7ca034a55fe198 */
    3.41020788139524715063e-07,  /* 0x3e96e2b149990227 */
    1.00582838631232552824e-07,  /* 0x3e7b00000294592c */
    3.68439433859276640065e-07,  /* 0x3e98b9bdc442620e */
    2.20403078342388012027e-07,  /* 0x3e8d94fdfabf3e4e */
    1.62841467098298142534e-07,  /* 0x3e85db30b145ad9a */
    2.25325348296680733838e-07,  /* 0x3e8e3e1eb95022b0 */
    4.37462238226421614339e-07,  /* 0x3e9d5b8b45442bd6 */
    3.52055880555040706500e-07,  /* 0x3e97a046231ecd2e */
    4.75614398494781776825e-07,  /* 0x3e9feafe3ef55232 */
    3.60998399033215317516e-07,  /* 0x3e9839e7bfd78267 */
    3.79292434611513945954e-08,  /* 0x3e645cf49d6fa900 */
    1.29859015528549300061e-08,  /* 0x3e4be3132b27f380 */
    3.15927546985474913188e-07,  /* 0x3e9533980bb84f9f */
    2.28533679887379668031e-08,  /* 0x3e5889e2ce3ba390 */
    1.17222541823553133877e-07,  /* 0x3e7f7778c3ad0cc8 */
    1.51991208405464415857e-07,  /* 0x3e846660cec4eba2 */
    1.56958239325240655564e-07}; /* 0x3e85110b4611a626 */

  /* Some constants and split constants. */

  static double pi = 3.1415926535897932e+00, /* 0x400921fb54442d18 */
             piby2 = 1.5707963267948966e+00, /* 0x3ff921fb54442d18 */
             piby4 = 7.8539816339744831e-01, /* 0x3fe921fb54442d18 */
       three_piby4 = 2.3561944901923449e+00, /* 0x4002d97c7f3321d2 */
           pi_head = 3.1415926218032836e+00, /* 0x400921fb50000000 */
           pi_tail = 3.1786509547056392e-08, /* 0x3e6110b4611a6263 */
        piby2_head = 1.5707963267948965e+00, /* 0x3ff921fb54442d18 */
        piby2_tail = 6.1232339957367660e-17; /* 0x3c91a62633145c07 */

  double u, v, vbyu, q1, q2, s, u1, vu1, u2, vu2, uu, c, r;
  unsigned int swap_vu, index, xzero, yzero, xnan, ynan, xinf, yinf;
  int m, xexp, yexp, diffexp;

  /* Find properties of arguments x and y. */

  uint64_t ux, ui, aux, xneg, uy, auy, yneg;

  GET_BITS_DP64(x, ux);
  GET_BITS_DP64(y, uy);
  aux = ux & ~SIGNBIT_DP64;
  auy = uy & ~SIGNBIT_DP64;
  xexp = (int)((ux & EXPBITS_DP64) >> EXPSHIFTBITS_DP64);
  yexp = (int)((uy & EXPBITS_DP64) >> EXPSHIFTBITS_DP64);
  xneg = ux & SIGNBIT_DP64;
  yneg = uy & SIGNBIT_DP64;
  xzero = (aux == 0);
  yzero = (auy == 0);
  xnan = (aux > PINFBITPATT_DP64);
  ynan = (auy > PINFBITPATT_DP64);
  xinf = (aux == PINFBITPATT_DP64);
  yinf = (auy == PINFBITPATT_DP64);

  diffexp = yexp - xexp;

  /* Special cases */

  /* IEEE 754: If either x or y is NaN, return NaN */
  if (xnan || ynan) {
#ifdef WINDOWS
    /*
     * Branchless sNaN vs qNaN handling:
     * sNaN (quiet bit = 0): raise FE_INVALID
     * qNaN (quiet bit = 1): no exception
     */
    int is_snan = (xnan && !(aux & QNAN_MASK_64)) ||
                  (ynan && !(auy & QNAN_MASK_64));
    return __alm_handle_error(
        (xnan ? ux : uy) | QNAN_MASK_64,
        is_snan ? AMD_F_INVALID : AMD_F_NONE
    );
#else
    return x + y; /* Propagate NaN, raise invalid for sNaN */
#endif
  }
  else if (yzero)
    { /* Zero y gives +-0 for positive x
         and +-pi for negative x */
      if (xneg)
    {

      if (yneg) return -pi;//return val_with_flags(-pi,AMD_F_INEXACT);
          else return pi; //val_with_flags(pi,AMD_F_INEXACT);
    }
      else return y;
    }
  else if (xzero)
    { /* Zero x gives +- pi/2
         depending on sign of y */
      if (yneg) return -piby2;//val_with_flags(-piby2,AMD_F_INEXACT);
      else return piby2;//val_with_flags(piby2,AMD_F_INEXACT);
    }

  /* Scale up both x and y if they are both below 1/4.
     This avoids any possible later denormalised arithmetic. */

  if ((xexp < 1021 && yexp < 1021))
    {
      scaleUpDouble1024(ux, &ux);
      scaleUpDouble1024(uy, &uy);
      PUT_BITS_DP64(ux, x);
      PUT_BITS_DP64(uy, y);
      xexp = (int)((ux & EXPBITS_DP64) >> EXPSHIFTBITS_DP64);
      yexp = (int)((uy & EXPBITS_DP64) >> EXPSHIFTBITS_DP64);
      diffexp = yexp - xexp;
    }

  if (diffexp > 56)
    { /* abs(y)/abs(x) > 2^56 => arctan(x/y)
         is insignificant compared to piby2 */
      if (yneg) return -piby2; //return val_with_flags(-piby2,AMD_F_INEXACT);
      else return piby2; //return val_with_flags(piby2,AMD_F_INEXACT);
    }
  else if (diffexp < -28 && (!xneg))
    { /* x positive and dominant over y by a factor of 2^28.
         In this case atan(y/x) is y/x to machine accuracy. */

      if (diffexp < -1074) /* Result underflows */
        {
          /* Raise FE_UNDERFLOW on all platforms */
          if (yneg)
            return __alm_handle_error(NEG_ZERO_F64, AMD_F_INEXACT | AMD_F_UNDERFLOW);
          else
            return __alm_handle_error(POS_ZERO_F64, AMD_F_INEXACT | AMD_F_UNDERFLOW);
        }
      else
        {
          if (diffexp < -1022)
            {
              /* Result will likely be denormalized */
              y = scaleDouble_1(y, 100);
              y /= x;
              /* Now y is 2^100 times the true result. Scale it back down. */
              GET_BITS_DP64(y, uy);
              scaleDownDouble(uy, 100, &uy);
              PUT_BITS_DP64(uy, y);
              return y;
             }
          else
            return y / x;
        }
    }
  else if (diffexp < -56 && xneg)
    { /* abs(x)/abs(y) > 2^56 and x < 0 => arctan(y/x)
         is insignificant compared to pi */
    if (yneg) return -pi; // return val_with_flags(-pi,AMD_F_INEXACT);
    else return pi; // return val_with_flags(pi,AMD_F_INEXACT);
    }
  else if (yinf && xinf)
    { /* If abs(x) and abs(y) are both infinity
         return +-pi/4 or +- 3pi/4 according to
         signs.  */
    if (xneg)
      {
      if (yneg) return -three_piby4; // return val_with_flags(-three_piby4,AMD_F_INEXACT);
      else return three_piby4;//return val_with_flags(three_piby4,AMD_F_INEXACT);
      }
    else
      {
      if (yneg) return -piby4; // return val_with_flags(-piby4,AMD_F_INEXACT);
      else return piby4; //return val_with_flags(piby4,AMD_F_INEXACT);
      }
    }

  /* General case: take absolute values of arguments */

  u = x; v = y;
  if (xneg) u = -x;
  if (yneg) v = -y;

  /* Swap u and v if necessary to obtain 0 < v < u. Compute v/u. */

  swap_vu = (u < v);
  if (swap_vu) { uu = u; u = v; v = uu; }
  vbyu = v/u;

  if (vbyu > 0.0625)
    { /* General values of v/u. Use a look-up
         table and series expansion. */

      index = (unsigned int)(256*vbyu + 0.5);
      q1 = atan_jby256_lead[index-16];
      q2 = atan_jby256_tail[index-16];
      c = index*1./256;
      GET_BITS_DP64(u, ui);
      m = (int)((ui & EXPBITS_DP64) >> EXPSHIFTBITS_DP64) - EXPBIAS_DP64;
      u = scaleDouble_2(u,-m);
      v = scaleDouble_2(v,-m);
      GET_BITS_DP64(u, ui);
      PUT_BITS_DP64(0xfffffffff8000000 & ui, u1); /* 26 leading bits of u */
      u2 = u - u1;

      r = ((v-c*u1)-c*u2)/(u+c*v);

      /* Polynomial approximation to atan(r) */

      s = r*r;
      q2 = q2 + r - r*(s * (0.33333333333224095522 - s*(0.19999918038989143496)));
    }
  else if (vbyu < 1.e-8)
    { /* v/u is small enough that atan(v/u) = v/u */
      q1 = 0.0;
      q2 = vbyu;
    }
  else  /* vbyu <= 0.0625 */
    {
      /* Small values of v/u. Use a series expansion
     computed carefully to minimise cancellation */

      GET_BITS_DP64(u, ui);
      PUT_BITS_DP64(0xffffffff00000000 & ui, u1);
      GET_BITS_DP64(vbyu, ui);
      PUT_BITS_DP64(0xffffffff00000000 & ui, vu1);
      u2 = u - u1;
      vu2 = vbyu - vu1;

      q1 = 0.0;
      s  = vbyu*vbyu;
      q2 = vbyu +
    ((((v - u1*vu1) - u2*vu1) - u*vu2)/u -
     (vbyu*s*(0.33333333333333170500 -
          s*(0.19999999999393223405 -
             s*(0.14285713561807169030 -
            s*(0.11110736283514525407 -
               s*(0.90029810285449784439E-01)))))));
    }

  /* Tidy-up according to which quadrant the arguments lie in */

  if (swap_vu) {q1 = piby2_head - q1; q2 = piby2_tail - q2;}
  if (xneg) {q1 = pi_head - q1; q2 = pi_tail - q2;}
  q1 = q1 + q2;

  if (yneg) q1 = - q1;

  return q1;
}



/* ---- aocl-libm-ose src/ref/sinh.c ---- */


double ALM_PROTO_REF(sinh)(double x)
{
  /*
    After dealing with special cases the computation is split into
    regions as follows:

    abs(x) >= max_sinh_arg:
    sinh(x) = sign(x)*Inf

    abs(x) >= small_threshold:
    sinh(x) = sign(x)*exp(abs(x))/2 computed using the
    splitexp and scaleDouble functions as for exp_amd().

    abs(x) < small_threshold:
    compute p = exp(y) - 1 and then z = 0.5*(p+(p/(p+1.0)))
    sinh(x) is then sign(x)*z.                             */

  static const double
    max_sinh_arg = 7.10475860073943977113e+02, /* 0x408633ce8fb9f87e */
    thirtytwo_by_log2 = 4.61662413084468283841e+01, /* 0x40471547652b82fe */
    log2_by_32_lead = 2.16608493356034159660e-02, /* 0x3f962e42fe000000 */
    log2_by_32_tail = 5.68948749532545630390e-11, /* 0x3dcf473de6af278e */
    small_threshold = 8*BASEDIGITS_DP64*0.30102999566398119521373889;
  /* (8*BASEDIGITS_DP64*log10of2) ' exp(-x) insignificant c.f. exp(x) */

  /* Lead and tail tabulated values of sinh(i) and cosh(i)
     for i = 0,...,36. The lead part has 26 leading bits. */

  static const double sinh_lead[37] = {
    0.00000000000000000000e+00,  /* 0x0000000000000000 */
    1.17520117759704589844e+00,  /* 0x3ff2cd9fc0000000 */
    3.62686038017272949219e+00,  /* 0x400d03cf60000000 */
    1.00178747177124023438e+01,  /* 0x40240926e0000000 */
    2.72899169921875000000e+01,  /* 0x403b4a3800000000 */
    7.42032089233398437500e+01,  /* 0x40528d0160000000 */
    2.01713153839111328125e+02,  /* 0x406936d228000000 */
    5.48316116333007812500e+02,  /* 0x4081228768000000 */
    1.49047882080078125000e+03,  /* 0x409749ea50000000 */
    4.05154187011718750000e+03,  /* 0x40afa71570000000 */
    1.10132326660156250000e+04,  /* 0x40c5829dc8000000 */
    2.99370708007812500000e+04,  /* 0x40dd3c4488000000 */
    8.13773945312500000000e+04,  /* 0x40f3de1650000000 */
    2.21206695312500000000e+05,  /* 0x410b00b590000000 */
    6.01302140625000000000e+05,  /* 0x412259ac48000000 */
    1.63450865625000000000e+06,  /* 0x4138f0cca8000000 */
    4.44305525000000000000e+06,  /* 0x4150f2ebd0000000 */
    1.20774762500000000000e+07,  /* 0x4167093488000000 */
    3.28299845000000000000e+07,  /* 0x417f4f2208000000 */
    8.92411500000000000000e+07,  /* 0x419546d8f8000000 */
    2.42582596000000000000e+08,  /* 0x41aceb0888000000 */
    6.59407856000000000000e+08,  /* 0x41c3a6e1f8000000 */
    1.79245641600000000000e+09,  /* 0x41dab5adb8000000 */
    4.87240166400000000000e+09,  /* 0x41f226af30000000 */
    1.32445608960000000000e+10,  /* 0x4208ab7fb0000000 */
    3.60024494080000000000e+10,  /* 0x4220c3d390000000 */
    9.78648043520000000000e+10,  /* 0x4236c93268000000 */
    2.66024116224000000000e+11,  /* 0x424ef822f0000000 */
    7.23128516608000000000e+11,  /* 0x42650bba30000000 */
    1.96566712320000000000e+12,  /* 0x427c9aae40000000 */
    5.34323724288000000000e+12,  /* 0x4293704708000000 */
    1.45244246507520000000e+13,  /* 0x42aa6b7658000000 */
    3.94814795284480000000e+13,  /* 0x42c1f43fc8000000 */
    1.07321789251584000000e+14,  /* 0x42d866f348000000 */
    2.91730863685632000000e+14,  /* 0x42f0953e28000000 */
    7.93006722514944000000e+14,  /* 0x430689e220000000 */
    2.15561576592179200000e+15}; /* 0x431ea215a0000000 */

  static const double sinh_tail[37] = {
    0.00000000000000000000e+00,  /* 0x0000000000000000 */
    1.60467555584448807892e-08,  /* 0x3e513ae6096a0092 */
    2.76742892754807136947e-08,  /* 0x3e5db70cfb79a640 */
    2.09697499555224576530e-07,  /* 0x3e8c2526b66dc067 */
    2.04940252448908240062e-07,  /* 0x3e8b81b18647f380 */
    1.65444891522700935932e-06,  /* 0x3ebbc1cdd1e1eb08 */
    3.53116789999998198721e-06,  /* 0x3ecd9f201534fb09 */
    6.94023870987375490695e-06,  /* 0x3edd1c064a4e9954 */
    4.98876893611587449271e-06,  /* 0x3ed4eca65d06ea74 */
    3.19656024605152215752e-05,  /* 0x3f00c259bcc0ecc5 */
    2.08687768377236501204e-04,  /* 0x3f2b5a6647cf9016 */
    4.84668088325403796299e-05,  /* 0x3f09691adefb0870 */
    1.17517985422733832468e-03,  /* 0x3f53410fc29cde38 */
    6.90830086959560562415e-04,  /* 0x3f46a31a50b6fb3c */
    1.45697262451506548420e-03,  /* 0x3f57defc71805c40 */
    2.99859023684906737806e-02,  /* 0x3f9eb49fd80e0bab */
    1.02538800507941396667e-02,  /* 0x3f84fffc7bcd5920 */
    1.26787628407699110022e-01,  /* 0x3fc03a93b6c63435 */
    6.86652479544033744752e-02,  /* 0x3fb1940bb255fd1c */
    4.81593627621056619148e-01,  /* 0x3fded26e14260b50 */
    1.70489513795397629181e+00,  /* 0x3ffb47401fc9f2a2 */
    1.12416073482258713767e+01,  /* 0x40267bb3f55634f1 */
    7.06579578070110514432e+00,  /* 0x401c435ff8194ddc */
    5.91244512999659974639e+01,  /* 0x404d8fee052ba63a */
    1.68921736147050694399e+02,  /* 0x40651d7edccde3f6 */
    2.60692936262073658327e+02,  /* 0x40704b1644557d1a */
    3.62419382134885609048e+02,  /* 0x4076a6b5ca0a9dc4 */
    4.07689930834187271103e+03,  /* 0x40afd9cc72249aba */
    1.55377375868385224749e+04,  /* 0x40ce58de693edab5 */
    2.53720210371943067003e+04,  /* 0x40d8c70158ac6363 */
    4.78822310734952334315e+04,  /* 0x40e7614764f43e20 */
    1.81871712615542812273e+05,  /* 0x4106337db36fc718 */
    5.62892347580489004031e+05,  /* 0x41212d98b1f611e2 */
    6.41374032312148716301e+05,  /* 0x412392bc108b37cc */
    7.57809544070145115256e+06,  /* 0x415ce87bdc3473dc */
    3.64177136406482197344e+06,  /* 0x414bc8d5ae99ad14 */
    7.63580561355670914054e+06}; /* 0x415d20d76744835c */

  static const double cosh_lead[37] = {
    1.00000000000000000000e+00,  /* 0x3ff0000000000000 */
    1.54308062791824340820e+00,  /* 0x3ff8b07550000000 */
    3.76219564676284790039e+00,  /* 0x400e18fa08000000 */
    1.00676617622375488281e+01,  /* 0x402422a490000000 */
    2.73082327842712402344e+01,  /* 0x403b4ee858000000 */
    7.42099475860595703125e+01,  /* 0x40528d6fc8000000 */
    2.01715633392333984375e+02,  /* 0x406936e678000000 */
    5.48317031860351562500e+02,  /* 0x4081228948000000 */
    1.49047915649414062500e+03,  /* 0x409749eaa8000000 */
    4.05154199218750000000e+03,  /* 0x40afa71580000000 */
    1.10132329101562500000e+04,  /* 0x40c5829dd0000000 */
    2.99370708007812500000e+04,  /* 0x40dd3c4488000000 */
    8.13773945312500000000e+04,  /* 0x40f3de1650000000 */
    2.21206695312500000000e+05,  /* 0x410b00b590000000 */
    6.01302140625000000000e+05,  /* 0x412259ac48000000 */
    1.63450865625000000000e+06,  /* 0x4138f0cca8000000 */
    4.44305525000000000000e+06,  /* 0x4150f2ebd0000000 */
    1.20774762500000000000e+07,  /* 0x4167093488000000 */
    3.28299845000000000000e+07,  /* 0x417f4f2208000000 */
    8.92411500000000000000e+07,  /* 0x419546d8f8000000 */
    2.42582596000000000000e+08,  /* 0x41aceb0888000000 */
    6.59407856000000000000e+08,  /* 0x41c3a6e1f8000000 */
    1.79245641600000000000e+09,  /* 0x41dab5adb8000000 */
    4.87240166400000000000e+09,  /* 0x41f226af30000000 */
    1.32445608960000000000e+10,  /* 0x4208ab7fb0000000 */
    3.60024494080000000000e+10,  /* 0x4220c3d390000000 */
    9.78648043520000000000e+10,  /* 0x4236c93268000000 */
    2.66024116224000000000e+11,  /* 0x424ef822f0000000 */
    7.23128516608000000000e+11,  /* 0x42650bba30000000 */
    1.96566712320000000000e+12,  /* 0x427c9aae40000000 */
    5.34323724288000000000e+12,  /* 0x4293704708000000 */
    1.45244246507520000000e+13,  /* 0x42aa6b7658000000 */
    3.94814795284480000000e+13,  /* 0x42c1f43fc8000000 */
    1.07321789251584000000e+14,  /* 0x42d866f348000000 */
    2.91730863685632000000e+14,  /* 0x42f0953e28000000 */
    7.93006722514944000000e+14,  /* 0x430689e220000000 */
    2.15561576592179200000e+15}; /* 0x431ea215a0000000 */

  static const double cosh_tail[37] = {
    0.00000000000000000000e+00,  /* 0x0000000000000000 */
    6.89700037027478056904e-09,  /* 0x3e3d9f5504c2bd28 */
    4.43207835591715833630e-08,  /* 0x3e67cb66f0a4c9fd */
    2.33540217013828929694e-07,  /* 0x3e8f58617928e588 */
    5.17452463948269748331e-08,  /* 0x3e6bc7d000c38d48 */
    9.38728274131605919153e-07,  /* 0x3eaf7f9d4e329998 */
    2.73012191010840495544e-06,  /* 0x3ec6e6e464885269 */
    3.29486051438996307950e-06,  /* 0x3ecba3a8b946c154 */
    4.75803746362771416375e-06,  /* 0x3ed3f4e76110d5a4 */
    3.33050940471947692369e-05,  /* 0x3f017622515a3e2b */
    9.94707313972136215365e-06,  /* 0x3ee4dc4b528af3d0 */
    6.51685096227860253398e-05,  /* 0x3f11156278615e10 */
    1.18132406658066663359e-03,  /* 0x3f535ad50ed821f5 */
    6.93090416366541877541e-04,  /* 0x3f46b61055f2935c */
    1.45780415323416845386e-03,  /* 0x3f57e2794a601240 */
    2.99862082708111758744e-02,  /* 0x3f9eb4b45f6aadd3 */
    1.02539925859688602072e-02,  /* 0x3f85000b967b3698 */
    1.26787669807076286421e-01,  /* 0x3fc03a940fadc092 */
    6.86652631843830962843e-02,  /* 0x3fb1940bf3bf874c */
    4.81593633223853068159e-01,  /* 0x3fded26e1a2a2110 */
    1.70489514001513020602e+00,  /* 0x3ffb4740205796d6 */
    1.12416073489841270572e+01,  /* 0x40267bb3f55cb85d */
    7.06579578098005001152e+00,  /* 0x401c435ff81e18ac */
    5.91244513000686140458e+01,  /* 0x404d8fee052bdea4 */
    1.68921736147088438429e+02,  /* 0x40651d7edccde926 */
    2.60692936262087528121e+02,  /* 0x40704b1644557e0e */
    3.62419382134890611269e+02,  /* 0x4076a6b5ca0a9e1c */
    4.07689930834187453002e+03,  /* 0x40afd9cc72249abe */
    1.55377375868385224749e+04,  /* 0x40ce58de693edab5 */
    2.53720210371943103382e+04,  /* 0x40d8c70158ac6364 */
    4.78822310734952334315e+04,  /* 0x40e7614764f43e20 */
    1.81871712615542812273e+05,  /* 0x4106337db36fc718 */
    5.62892347580489004031e+05,  /* 0x41212d98b1f611e2 */
    6.41374032312148716301e+05,  /* 0x412392bc108b37cc */
    7.57809544070145115256e+06,  /* 0x415ce87bdc3473dc */
    3.64177136406482197344e+06,  /* 0x414bc8d5ae99ad14 */
    7.63580561355670914054e+06}; /* 0x415d20d76744835c */

  unsigned long long ux, aux, xneg;
  double y, z, z1, z2;
  int m;

  /* Special cases */

  GET_BITS_DP64(x, ux);
  aux = ux & ~SIGNBIT_DP64;
  if (aux < 0x3e30000000000000) /* |x| small enough that sinh(x) = x */
    {
        if (aux == 0x0000000000000000)
        {
            /* x is +/-zero. Return the same zero. */
            return x;
        }
        else
        {
           
#ifdef WINDOWS
        return x;
#else
        return __alm_handle_error(ux, AMD_F_INEXACT|AMD_F_UNDERFLOW);
#endif
        }
    }
  else if (aux == 0x7ff0000000000000) /* |x| is Inf */
    {
        return x;
    }
  else if  (aux > 0x7ff0000000000000) /* |x| is NaN */
    {
#ifdef WINDOWS
		return __alm_handle_error(ux|QNANBITPATT_DP64, AMD_F_NONE);
#else
        return x+x;
#endif
    }


  xneg = (aux != ux);

  y = x;
  if (xneg) y = -x;

  if (y >= max_sinh_arg)
    {
      /* Return +/-infinity with overflow flag */
      if (xneg)
		  return __alm_handle_error(NINFBITPATT_DP64, AMD_F_OVERFLOW);
      else
		  return __alm_handle_error(PINFBITPATT_DP64, AMD_F_OVERFLOW);

    }
  else if (y >= small_threshold)
    {
      /* In this range y is large enough so that
         the negative exponential is negligible,
         so sinh(y) is approximated by sign(x)*exp(y)/2. The
         code below is an inlined version of that from
         exp() with two changes (it operates on
         y instead of x, and the division by 2 is
         done by reducing m by 1). */

      splitexp(y, 1.0, thirtytwo_by_log2, log2_by_32_lead,
               log2_by_32_tail, &m, &z1, &z2);
      m -= 1;

      if (m >= EMIN_DP64 && m <= EMAX_DP64)
        z = scaleDouble_1((z1+z2),m);
      else
        z = scaleDouble_2((z1+z2),m);
    }
  else
    {
      /* In this range we find the integer part y0 of y
         and the increment dy = y - y0. We then compute

         z = sinh(y) = sinh(y0)cosh(dy) + cosh(y0)sinh(dy)

         where sinh(y0) and cosh(y0) are tabulated above. */

      int ind;
      double dy, dy2, sdy, cdy, sdy1, sdy2;

      ind = (int)y;
      dy = y - ind;

      dy2 = dy*dy;
      sdy = dy*dy2*(0.166666666666666667013899e0 +
                    (0.833333333333329931873097e-2 +
                     (0.198412698413242405162014e-3 +
                      (0.275573191913636406057211e-5 +
                       (0.250521176994133472333666e-7 +
                        (0.160576793121939886190847e-9 +
                         0.7746188980094184251527126e-12*dy2)*dy2)*dy2)*dy2)*dy2)*dy2);

      cdy = dy2*(0.500000000000000005911074e0 +
                 (0.416666666666660876512776e-1 +
                  (0.138888888889814854814536e-2 +
                   (0.248015872460622433115785e-4 +
                    (0.275573350756016588011357e-6 +
                     (0.208744349831471353536305e-8 +
                      0.1163921388172173692062032e-10*dy2)*dy2)*dy2)*dy2)*dy2)*dy2);

      /* At this point sinh(dy) is approximated by dy + sdy.
	 Shift some significant bits from dy to sdy. */

      GET_BITS_DP64(dy, ux);
      ux &= 0xfffffffff8000000;
      PUT_BITS_DP64(ux, sdy1);
      sdy2 = sdy + (dy - sdy1);

      z = ((((((cosh_tail[ind]*sdy2 + sinh_tail[ind]*cdy)
	       + cosh_tail[ind]*sdy1) + sinh_tail[ind])
	     + cosh_lead[ind]*sdy2) + sinh_lead[ind]*cdy)
	   + cosh_lead[ind]*sdy1) + sinh_lead[ind];
    }

  if (xneg) z = - z;
  return z;
}



/* ---- aocl-libm-ose src/ref/cosh.c ---- */



double ALM_PROTO_REF(cosh)(double x)
{
  /*
    Derived from sinh subroutine
    
    After dealing with special cases the computation is split into
    regions as follows:

    abs(x) >= max_cosh_arg:
    cosh(x) = sign(x)*Inf

    abs(x) >= small_threshold:
    cosh(x) = sign(x)*exp(abs(x))/2 computed using the
    splitexp and scaleDouble functions as for exp_amd().

    abs(x) < small_threshold:
    compute p = exp(y) - 1 and then z = 0.5*(p+(p/(p+1.0)))
    cosh(x) is then sign(x)*z.                             */

  static const double
    max_cosh_arg = 7.10475860073943977113e+02, /* 0x408633ce8fb9f87e */
    thirtytwo_by_log2 = 4.61662413084468283841e+01, /* 0x40471547652b82fe */
    log2_by_32_lead = 2.16608493356034159660e-02, /* 0x3f962e42fe000000 */
    log2_by_32_tail = 5.68948749532545630390e-11, /* 0x3dcf473de6af278e */
//    small_threshold = 8*BASEDIGITS_DP64*0.30102999566398119521373889;
    small_threshold = 20.0;
  /* (8*BASEDIGITS_DP64*log10of2) ' exp(-x) insignificant c.f. exp(x) */

  /* Lead and tail tabulated values of sinh(i) and cosh(i) 
     for i = 0,...,36. The lead part has 26 leading bits. */

  static const double sinh_lead[   37] = {
    0.00000000000000000000e+00,  /* 0x0000000000000000 */
    1.17520117759704589844e+00,  /* 0x3ff2cd9fc0000000 */
    3.62686038017272949219e+00,  /* 0x400d03cf60000000 */
    1.00178747177124023438e+01,  /* 0x40240926e0000000 */
    2.72899169921875000000e+01,  /* 0x403b4a3800000000 */
    7.42032089233398437500e+01,  /* 0x40528d0160000000 */
    2.01713153839111328125e+02,  /* 0x406936d228000000 */
    5.48316116333007812500e+02,  /* 0x4081228768000000 */
    1.49047882080078125000e+03,  /* 0x409749ea50000000 */
    4.05154187011718750000e+03,  /* 0x40afa71570000000 */
    1.10132326660156250000e+04,  /* 0x40c5829dc8000000 */
    2.99370708007812500000e+04,  /* 0x40dd3c4488000000 */
    8.13773945312500000000e+04,  /* 0x40f3de1650000000 */
    2.21206695312500000000e+05,  /* 0x410b00b590000000 */
    6.01302140625000000000e+05,  /* 0x412259ac48000000 */
    1.63450865625000000000e+06,  /* 0x4138f0cca8000000 */
    4.44305525000000000000e+06,  /* 0x4150f2ebd0000000 */
    1.20774762500000000000e+07,  /* 0x4167093488000000 */
    3.28299845000000000000e+07,  /* 0x417f4f2208000000 */
    8.92411500000000000000e+07,  /* 0x419546d8f8000000 */
    2.42582596000000000000e+08,  /* 0x41aceb0888000000 */
    6.59407856000000000000e+08,  /* 0x41c3a6e1f8000000 */
    1.79245641600000000000e+09,  /* 0x41dab5adb8000000 */
    4.87240166400000000000e+09,  /* 0x41f226af30000000 */
    1.32445608960000000000e+10,  /* 0x4208ab7fb0000000 */
    3.60024494080000000000e+10,  /* 0x4220c3d390000000 */
    9.78648043520000000000e+10,  /* 0x4236c93268000000 */
    2.66024116224000000000e+11,  /* 0x424ef822f0000000 */
    7.23128516608000000000e+11,  /* 0x42650bba30000000 */
    1.96566712320000000000e+12,  /* 0x427c9aae40000000 */
    5.34323724288000000000e+12,  /* 0x4293704708000000 */
    1.45244246507520000000e+13,  /* 0x42aa6b7658000000 */
    3.94814795284480000000e+13,  /* 0x42c1f43fc8000000 */
    1.07321789251584000000e+14,  /* 0x42d866f348000000 */
    2.91730863685632000000e+14,  /* 0x42f0953e28000000 */
    7.93006722514944000000e+14,  /* 0x430689e220000000 */
    2.15561576592179200000e+15}; /* 0x431ea215a0000000 */

  static const double sinh_tail[   37] = {
    0.00000000000000000000e+00,  /* 0x0000000000000000 */
    1.60467555584448807892e-08,  /* 0x3e513ae6096a0092 */
    2.76742892754807136947e-08,  /* 0x3e5db70cfb79a640 */
    2.09697499555224576530e-07,  /* 0x3e8c2526b66dc067 */
    2.04940252448908240062e-07,  /* 0x3e8b81b18647f380 */
    1.65444891522700935932e-06,  /* 0x3ebbc1cdd1e1eb08 */
    3.53116789999998198721e-06,  /* 0x3ecd9f201534fb09 */
    6.94023870987375490695e-06,  /* 0x3edd1c064a4e9954 */
    4.98876893611587449271e-06,  /* 0x3ed4eca65d06ea74 */
    3.19656024605152215752e-05,  /* 0x3f00c259bcc0ecc5 */
    2.08687768377236501204e-04,  /* 0x3f2b5a6647cf9016 */
    4.84668088325403796299e-05,  /* 0x3f09691adefb0870 */
    1.17517985422733832468e-03,  /* 0x3f53410fc29cde38 */
    6.90830086959560562415e-04,  /* 0x3f46a31a50b6fb3c */
    1.45697262451506548420e-03,  /* 0x3f57defc71805c40 */
    2.99859023684906737806e-02,  /* 0x3f9eb49fd80e0bab */
    1.02538800507941396667e-02,  /* 0x3f84fffc7bcd5920 */
    1.26787628407699110022e-01,  /* 0x3fc03a93b6c63435 */
    6.86652479544033744752e-02,  /* 0x3fb1940bb255fd1c */
    4.81593627621056619148e-01,  /* 0x3fded26e14260b50 */
    1.70489513795397629181e+00,  /* 0x3ffb47401fc9f2a2 */
    1.12416073482258713767e+01,  /* 0x40267bb3f55634f1 */
    7.06579578070110514432e+00,  /* 0x401c435ff8194ddc */
    5.91244512999659974639e+01,  /* 0x404d8fee052ba63a */
    1.68921736147050694399e+02,  /* 0x40651d7edccde3f6 */
    2.60692936262073658327e+02,  /* 0x40704b1644557d1a */
    3.62419382134885609048e+02,  /* 0x4076a6b5ca0a9dc4 */
    4.07689930834187271103e+03,  /* 0x40afd9cc72249aba */
    1.55377375868385224749e+04,  /* 0x40ce58de693edab5 */
    2.53720210371943067003e+04,  /* 0x40d8c70158ac6363 */
    4.78822310734952334315e+04,  /* 0x40e7614764f43e20 */
    1.81871712615542812273e+05,  /* 0x4106337db36fc718 */
    5.62892347580489004031e+05,  /* 0x41212d98b1f611e2 */
    6.41374032312148716301e+05,  /* 0x412392bc108b37cc */
    7.57809544070145115256e+06,  /* 0x415ce87bdc3473dc */
    3.64177136406482197344e+06,  /* 0x414bc8d5ae99ad14 */
    7.63580561355670914054e+06}; /* 0x415d20d76744835c */

  static const double cosh_lead[   37] = {
    1.00000000000000000000e+00,  /* 0x3ff0000000000000 */
    1.54308062791824340820e+00,  /* 0x3ff8b07550000000 */
    3.76219564676284790039e+00,  /* 0x400e18fa08000000 */
    1.00676617622375488281e+01,  /* 0x402422a490000000 */
    2.73082327842712402344e+01,  /* 0x403b4ee858000000 */
    7.42099475860595703125e+01,  /* 0x40528d6fc8000000 */
    2.01715633392333984375e+02,  /* 0x406936e678000000 */
    5.48317031860351562500e+02,  /* 0x4081228948000000 */
    1.49047915649414062500e+03,  /* 0x409749eaa8000000 */
    4.05154199218750000000e+03,  /* 0x40afa71580000000 */
    1.10132329101562500000e+04,  /* 0x40c5829dd0000000 */
    2.99370708007812500000e+04,  /* 0x40dd3c4488000000 */
    8.13773945312500000000e+04,  /* 0x40f3de1650000000 */
    2.21206695312500000000e+05,  /* 0x410b00b590000000 */
    6.01302140625000000000e+05,  /* 0x412259ac48000000 */
    1.63450865625000000000e+06,  /* 0x4138f0cca8000000 */
    4.44305525000000000000e+06,  /* 0x4150f2ebd0000000 */
    1.20774762500000000000e+07,  /* 0x4167093488000000 */
    3.28299845000000000000e+07,  /* 0x417f4f2208000000 */
    8.92411500000000000000e+07,  /* 0x419546d8f8000000 */
    2.42582596000000000000e+08,  /* 0x41aceb0888000000 */
    6.59407856000000000000e+08,  /* 0x41c3a6e1f8000000 */
    1.79245641600000000000e+09,  /* 0x41dab5adb8000000 */
    4.87240166400000000000e+09,  /* 0x41f226af30000000 */
    1.32445608960000000000e+10,  /* 0x4208ab7fb0000000 */
    3.60024494080000000000e+10,  /* 0x4220c3d390000000 */
    9.78648043520000000000e+10,  /* 0x4236c93268000000 */
    2.66024116224000000000e+11,  /* 0x424ef822f0000000 */
    7.23128516608000000000e+11,  /* 0x42650bba30000000 */
    1.96566712320000000000e+12,  /* 0x427c9aae40000000 */
    5.34323724288000000000e+12,  /* 0x4293704708000000 */
    1.45244246507520000000e+13,  /* 0x42aa6b7658000000 */
    3.94814795284480000000e+13,  /* 0x42c1f43fc8000000 */
    1.07321789251584000000e+14,  /* 0x42d866f348000000 */
    2.91730863685632000000e+14,  /* 0x42f0953e28000000 */
    7.93006722514944000000e+14,  /* 0x430689e220000000 */
    2.15561576592179200000e+15}; /* 0x431ea215a0000000 */

  static const double cosh_tail[   37] = {
    0.00000000000000000000e+00,  /* 0x0000000000000000 */
    6.89700037027478056904e-09,  /* 0x3e3d9f5504c2bd28 */
    4.43207835591715833630e-08,  /* 0x3e67cb66f0a4c9fd */
    2.33540217013828929694e-07,  /* 0x3e8f58617928e588 */
    5.17452463948269748331e-08,  /* 0x3e6bc7d000c38d48 */
    9.38728274131605919153e-07,  /* 0x3eaf7f9d4e329998 */
    2.73012191010840495544e-06,  /* 0x3ec6e6e464885269 */
    3.29486051438996307950e-06,  /* 0x3ecba3a8b946c154 */
    4.75803746362771416375e-06,  /* 0x3ed3f4e76110d5a4 */
    3.33050940471947692369e-05,  /* 0x3f017622515a3e2b */
    9.94707313972136215365e-06,  /* 0x3ee4dc4b528af3d0 */
    6.51685096227860253398e-05,  /* 0x3f11156278615e10 */
    1.18132406658066663359e-03,  /* 0x3f535ad50ed821f5 */
    6.93090416366541877541e-04,  /* 0x3f46b61055f2935c */
    1.45780415323416845386e-03,  /* 0x3f57e2794a601240 */
    2.99862082708111758744e-02,  /* 0x3f9eb4b45f6aadd3 */
    1.02539925859688602072e-02,  /* 0x3f85000b967b3698 */
    1.26787669807076286421e-01,  /* 0x3fc03a940fadc092 */
    6.86652631843830962843e-02,  /* 0x3fb1940bf3bf874c */
    4.81593633223853068159e-01,  /* 0x3fded26e1a2a2110 */
    1.70489514001513020602e+00,  /* 0x3ffb4740205796d6 */
    1.12416073489841270572e+01,  /* 0x40267bb3f55cb85d */
    7.06579578098005001152e+00,  /* 0x401c435ff81e18ac */
    5.91244513000686140458e+01,  /* 0x404d8fee052bdea4 */
    1.68921736147088438429e+02,  /* 0x40651d7edccde926 */
    2.60692936262087528121e+02,  /* 0x40704b1644557e0e */
    3.62419382134890611269e+02,  /* 0x4076a6b5ca0a9e1c */
    4.07689930834187453002e+03,  /* 0x40afd9cc72249abe */
    1.55377375868385224749e+04,  /* 0x40ce58de693edab5 */
    2.53720210371943103382e+04,  /* 0x40d8c70158ac6364 */
    4.78822310734952334315e+04,  /* 0x40e7614764f43e20 */
    1.81871712615542812273e+05,  /* 0x4106337db36fc718 */
    5.62892347580489004031e+05,  /* 0x41212d98b1f611e2 */
    6.41374032312148716301e+05,  /* 0x412392bc108b37cc */
    7.57809544070145115256e+06,  /* 0x415ce87bdc3473dc */
    3.64177136406482197344e+06,  /* 0x414bc8d5ae99ad14 */
    7.63580561355670914054e+06}; /* 0x415d20d76744835c */

  unsigned long long ux, aux, xneg;
  double y, z, z1, z2;
  int m;

  /* Special cases */

  GET_BITS_DP64(x, ux);
  aux = ux & ~SIGNBIT_DP64;
  if (aux < 0x3e30000000000000) /* |x| small enough that cosh(x) = 1 */
  {
      return 1.0;
  }
  else if (aux >= PINFBITPATT_DP64) /* |x| is NaN or Inf */
  {
      if (aux > PINFBITPATT_DP64) /* |x| is a NaN? */
      {
#ifdef WINDOWS
         return __alm_handle_error(ux|QNANBITPATT_DP64, AMD_F_NONE);
#else
         return x+x;
#endif
      }
      else    /* x is infinity */
	  {
		 PUT_BITS_DP64(PINFBITPATT_DP64, x);
         return x;
	  }
  }


  xneg = (aux != ux);

  y = x;
  if (xneg) y = -x;

  if (y >= max_cosh_arg)
      {
      /* Return +/-infinity with overflow flag */
         return __alm_handle_error(PINFBITPATT_DP64, AMD_F_OVERFLOW);
      }
  else if (y >= small_threshold)
    {
      /* In this range y is large enough so that
         the negative exponential is negligible,
         so cosh(y) is approximated by sign(x)*exp(y)/2. The
         code below is an inlined version of that from
         exp() with two changes (it operates on
         y instead of x, and the division by 2 is
         done by reducing m by 1). */

      splitexp(y, 1.0, thirtytwo_by_log2, log2_by_32_lead,
               log2_by_32_tail, &m, &z1, &z2);
      m -= 1;

      if (m >= EMIN_DP64 && m <= EMAX_DP64)
        z = scaleDouble_1((z1+z2),m);
      else
        z = scaleDouble_2((z1+z2),m);
    }
  else
    {
      /* In this range we find the integer part y0 of y 
         and the increment dy = y - y0. We then compute
 
         z = sinh(y) = sinh(y0)cosh(dy) + cosh(y0)sinh(dy)
         z = cosh(y) = cosh(y0)cosh(dy) + sinh(y0)sinh(dy)

         where sinh(y0) and cosh(y0) are tabulated above. */

      int ind;
      double dy, dy2, sdy, cdy;

      ind = (int)y;
      dy = y - ind;

      dy2 = dy*dy;
      sdy = dy*dy2*(0.166666666666666667013899e0 +
                    (0.833333333333329931873097e-2 +
                     (0.198412698413242405162014e-3 +
                      (0.275573191913636406057211e-5 +
                       (0.250521176994133472333666e-7 +
                        (0.160576793121939886190847e-9 +
                         0.7746188980094184251527126e-12*dy2)*dy2)*dy2)*dy2)*dy2)*dy2);

      cdy = dy2*(0.500000000000000005911074e0 +
                 (0.416666666666660876512776e-1 +
                  (0.138888888889814854814536e-2 +
                   (0.248015872460622433115785e-4 +
                    (0.275573350756016588011357e-6 +
                     (0.208744349831471353536305e-8 +
                      0.1163921388172173692062032e-10*dy2)*dy2)*dy2)*dy2)*dy2)*dy2);

      /* At this point sinh(dy) is approximated by dy + sdy, and cosh(dy) is approximated by 1 + cdy.
	 Shift some significant bits from dy to cdy. */
#if 0 
    double  sdy1,sdy2;
      GET_BITS_DP64(dy, ux);
      ux &= 0xfffffffff8000000;
      PUT_BITS_DP64(ux, sdy1);    // sdy1 is  upper 53-27=26 significant bits of dy.
      sdy2 = sdy + (dy - sdy1);   // sdy2 is  sdy + lower bits of dy

      z = ((((((cosh_tail[ind]*cdy + sinh_tail[ind]*sdy2) 
	       + sinh_tail[ind]*sdy1) + cosh_tail[ind])  
	     + cosh_lead[ind]*cdy) + sinh_lead[ind]*sdy2) 
	   + sinh_lead[ind]*sdy1) + cosh_lead[ind];
#else
      z = ((((((cosh_tail[ind]*cdy + sinh_tail[ind]*sdy) 
	       + sinh_tail[ind]*dy) + cosh_tail[ind])  
	     + cosh_lead[ind]*cdy) + sinh_lead[ind]*sdy) 
	   + sinh_lead[ind]*dy) + cosh_lead[ind];
#endif
    }

  return z;
}



/* ---- aocl-libm-ose src/ref/tanh.c ---- */


double ALM_PROTO_REF(tanh)(double x)
{
  /*
    The definition of tanh(x) is sinh(x)/cosh(x), which is also equivalent
    to the following three formulae:
    1.  (exp(x) - exp(-x))/(exp(x) + exp(-x))
    2.  (1 - (2/(exp(2*x) + 1 )))
    3.  (exp(2*x) - 1)/(exp(2*x) + 1)
    but computationally, some formulae are better on some ranges.
  */
  static const double
    thirtytwo_by_log2 = 4.61662413084468283841e+01, /* 0x40471547652b82fe */
    log2_by_32_lead = 2.16608493356034159660e-02, /* 0x3f962e42fe000000 */
    log2_by_32_tail = 5.68948749532545630390e-11, /* 0x3dcf473de6af278e */
    large_threshold = 20.0; /* 0x4034000000000000 */

  unsigned long long ux, aux, xneg;
  double y, z, p, z1, z2;
  int m;

  /* Special cases */

  GET_BITS_DP64(x, ux);
  aux = ux & ~SIGNBIT_DP64;
  if (aux < 0x3e30000000000000) /* |x| small enough that tanh(x) = x */
    {      
        if (aux == 0x0000000000000000)
        {
            /* x is +/-zero. Return the same zero. */
            return x;
        }
        else
        {
#ifdef WINDOWS
            return x;
#else
            return __alm_handle_error(ux, AMD_F_INEXACT|AMD_F_UNDERFLOW);
#endif
        }
    }
  else if  (aux > 0x7ff0000000000000) /* |x| is NaN */
    {
#ifdef WINDOWS
        return __alm_handle_error(ux|QNANBITPATT_DP64, AMD_F_NONE);
#else
        return x+x;
#endif
    }

  xneg = (aux != ux);

  y = x;
  if (xneg) y = -x;

  if (y > large_threshold)
    {
      /* If x is large then exp(-x) is negligible and
         formula 1 reduces to plus or minus 1.0 */
      z = 1.0;
    }
  else if (y <= 1.0)
    {
      double y2;
      y2 = y*y;
      if (y < 0.9)
        {
          /* Use a [3,3] Remez approximation on [0,0.9]. */
          z = y + y*y2*
            (-0.274030424656179760118928e0 +
             (-0.176016349003044679402273e-1 +
              (-0.200047621071909498730453e-3 -
               0.142077926378834722618091e-7*y2)*y2)*y2)/
            (0.822091273968539282568011e0 +
             (0.381641414288328849317962e0 +
              (0.201562166026937652780575e-1 +
               0.2091140262529164482568557e-3*y2)*y2)*y2);
        }
      else
        {
          /* Use a [3,3] Remez approximation on [0.9,1]. */
          z = y + y*y2*
            (-0.227793870659088295252442e0 +
             (-0.146173047288731678404066e-1 +
              (-0.165597043903549960486816e-3 -
               0.115475878996143396378318e-7*y2)*y2)*y2)/
            (0.683381611977295894959554e0 +
             (0.317204558977294374244770e0 +
              (0.167358775461896562588695e-1 +
               0.173076050126225961768710e-3*y2)*y2)*y2);
        }
    }
  else
    {
      /* Compute p = exp(2*y) + 1. The code is basically inlined
         from exp_amd. */

      splitexp(2*y, 1.0, thirtytwo_by_log2, log2_by_32_lead,
           log2_by_32_tail, &m, &z1, &z2);
      p = scaleDouble_2(z1 + z2, m) + 1.0;

      /* Now reconstruct tanh from p. */
      z = (1.0 - 2.0/p);
    }

  if (xneg) z = - z;
  return z;
}




/* ---- aocl-libm-ose src/ref/asinf.c ---- */



float ALM_PROTO_REF(asinf)(float x)
{
  /* Computes arcsin(x).
     The argument is first reduced by noting that arcsin(x)
     is invalid for abs(x) > 1 and arcsin(-x) = -arcsin(x).
     For denormal and small arguments arcsin(x) = x to machine
     accuracy. Remaining argument ranges are handled as follows.
     For abs(x) <= 0.5 use
     arcsin(x) = x + x^3*R(x^2)
     where R(x^2) is a rational minimax approximation to
     (arcsin(x) - x)/x^3.
     For abs(x) > 0.5 exploit the identity:
      arcsin(x) = pi/2 - 2*arcsin(sqrt(1-x)/2)
     together with the above rational approximation, and
     reconstruct the terms carefully.
    */

  /* Some constants and split constants. */

  static const float
    piby2_tail  = 7.5497894159e-08F, /* 0x33a22168 */
    hpiby2_head = 7.8539812565e-01F, /* 0x3f490fda */
    piby2       = 1.5707963705e+00F; /* 0x3fc90fdb */
  float u, v, y, s = 0.0F, r;
  int xexp, xnan, transform = 0;

  unsigned int ux, aux, xneg;
  GET_BITS_SP32(x, ux);
  aux = ux & ~SIGNBIT_SP32;
  xneg = (ux & SIGNBIT_SP32);
  xnan = (aux > PINFBITPATT_SP32);
  xexp = (int)((ux & EXPBITS_SP32) >> EXPSHIFTBITS_SP32) - EXPBIAS_SP32;

  /* Special cases */

  if (xnan)
    {
#ifdef WINDOWS
     return  __alm_handle_errorf(ux|0x00400000, AMD_F_NONE);
#else
      //return x + x; /* With invalid if it's a signalling NaN */
      if (ux & QNAN_MASK_32)
     return  __alm_handle_errorf(ux|0x00400000, AMD_F_NONE);
      else
     return  __alm_handle_errorf(ux|0x00400000, AMD_F_INVALID);
#endif
    }
  else if (xexp < -14)
  {
    /* y small enough that arcsin(x) = x */
#ifdef WINDOWS
    return x;//valf_with_flags(x, AMD_F_INEXACT);
#else
         if ((ux == SIGNBIT_SP32) || (ux == 0x0))
            return x;
	 else
            return  __alm_handle_errorf(ux, AMD_F_UNDERFLOW|AMD_F_INEXACT);
#endif
     }
  else if (xexp >= 0)
    {
      /* abs(x) >= 1.0 */
      if (x == 1.0F)
        return piby2; //valf_with_flags(piby2, AMD_F_INEXACT);
      else if (x == -1.0F)
        return -piby2; //valf_with_flags(-piby2, AMD_F_INEXACT);
      else
#ifdef WINDOWS
     return  __alm_handle_errorf(INDEFBITPATT_SP32, AMD_F_INVALID);
#else
     return  __alm_handle_errorf(INDEFBITPATT_SP32, AMD_F_INVALID);
        //return retval_errno_edom(x);
#endif
    }

  if (xneg) y = -x;
  else y = x;

  transform = (xexp >= -1); /* abs(x) >= 0.5 */

  if (transform)
    { /* Transform y into the range [0,0.5) */
      r = 0.5F*(1.0F - y);
#ifdef WINDOWS
      /* VC++ intrinsic call */
      _mm_store_ss(&s, _mm_sqrt_ss(_mm_load_ss(&r)));
#else
      /* Hammer sqrt instruction */
      asm volatile ("sqrtss %1, %0" : "=x" (s) : "x" (r));
#endif
      y = s;
    }
  else
    r = y*y;

  /* Use a rational approximation for [0.0, 0.5] */

  u=r*(0.184161606965100694821398249421F +
       (-0.0565298683201845211985026327361F +
	(-0.0133819288943925804214011424456F -
	 0.00396137437848476485201154797087F*r)*r)*r)/
    (1.10496961524520294485512696706F -
     0.836411276854206731913362287293F*r);

  if (transform)
    {
      /* Reconstruct asin carefully in transformed region */
      float c, s1, p, q;
      unsigned int us;
      GET_BITS_SP32(s, us);
      PUT_BITS_SP32(0xffff0000 & us, s1);
      c = (r-s1*s1)/(s+s1);
      p = 2.0F*s*u - (piby2_tail-2.0F*c);
      q = hpiby2_head - 2.0F*s1;
      v = hpiby2_head - (p-q);
    }
  else
    {
#ifdef WINDOWS
      /* Use a temporary variable to prevent VC++ rearranging
            y + y*u
         into
            y * (1 + u)
         and getting an incorrectly rounded result */
      float tmp;
      tmp = y * u;
      v = y + tmp;
#else
      v = y + y*u;
#endif
    }

  if (xneg) return -v;
  else return v;
}



/* ---- aocl-libm-ose src/ref/acosf.c ---- */



float ALM_PROTO_REF(acosf)(float x)
{
  /* Computes arccos(x).
     The argument is first reduced by noting that arccos(x)
     is invalid for abs(x) > 1. For denormal and small
     arguments arccos(x) = pi/2 to machine accuracy.
     Remaining argument ranges are handled as follows.
     For abs(x) <= 0.5 use
     arccos(x) = pi/2 - arcsin(x)
     = pi/2 - (x + x^3*R(x^2))
     where R(x^2) is a rational minimax approximation to
     (arcsin(x) - x)/x^3.
     For abs(x) > 0.5 exploit the identity:
     arccos(x) = pi - 2*arcsin(sqrt(1-x)/2)
     together with the above rational approximation, and
     reconstruct the terms carefully.
  */

  /* Some constants and split constants. */

  static const float
    piby2      = 1.5707963705e+00F; /* 0x3fc90fdb */
  static const double
    pi         = 3.1415926535897933e+00, /* 0x400921fb54442d18 */
    piby2_head = 1.5707963267948965580e+00, /* 0x3ff921fb54442d18 */
    piby2_tail = 6.12323399573676603587e-17; /* 0x3c91a62633145c07 */

  float u, y, s = 0.0F, r;
  int xexp, xnan, transform = 0;

  unsigned int ux, aux, xneg;

  GET_BITS_SP32(x, ux);
  aux = ux & ~SIGNBIT_SP32;
  xneg = (ux & SIGNBIT_SP32);
  xnan = (aux > PINFBITPATT_SP32);
  xexp = (int)((ux & EXPBITS_SP32) >> EXPSHIFTBITS_SP32) - EXPBIAS_SP32;

  /* Special cases */

  if (xnan)
    {
#ifdef WINDOWS
     return  __alm_handle_errorf(ux|0x00400000, AMD_F_NONE);
#else
      //return x + x; /* With invalid if it's a signalling NaN */
      if (ux & QNAN_MASK_32)
     return  __alm_handle_errorf(ux|0x00400000, AMD_F_NONE);
      else
     return  __alm_handle_errorf(ux|0x00400000, AMD_F_INVALID);


#endif
    }
  else if (xexp < -26)
    /* y small enough that arccos(x) = pi/2 */
    return piby2;//valf_with_flags(piby2, AMD_F_INEXACT);
  else if (xexp >= 0)
    { /* abs(x) >= 1.0 */
      if (x == 1.0F)
        return 0.0F;
      else if (x == -1.0F)
        return (float)pi;//valf_with_flags((float)pi, AMD_F_INEXACT);
      else
#ifdef WINDOWS
		return  __alm_handle_errorf(INDEFBITPATT_SP32, AMD_F_INVALID);
#else
        //return retval_errno_edom(x);
		return  __alm_handle_errorf(INDEFBITPATT_SP32, AMD_F_INVALID);
#endif
    }

  if (xneg) y = -x;
  else y = x;

  transform = (xexp >= -1); /* abs(x) >= 0.5 */

  if (transform)
    { /* Transform y into the range [0,0.5) */
      r = 0.5F*(1.0F - y);
#ifdef WINDOWS
      /* VC++ intrinsic call */
      _mm_store_ss(&s, _mm_sqrt_ss(_mm_load_ss(&r)));
#else
      /* Hammer sqrt instruction */
      asm volatile ("sqrtss %1, %0" : "=x" (s) : "x" (r));
#endif
      y = s;
    }
  else
    r = y*y;

  /* Use a rational approximation for [0.0, 0.5] */

  u=r*(0.184161606965100694821398249421F +
       (-0.0565298683201845211985026327361F +
	(-0.0133819288943925804214011424456F -
	 0.00396137437848476485201154797087F*r)*r)*r)/
    (1.10496961524520294485512696706F -
     0.836411276854206731913362287293F*r);

  if (transform)
    {
      /* Reconstruct acos carefully in transformed region */
      if (xneg){
        double ds = (double)s, dy = (double)y, du = (double)u;
        return (float)(pi - 2.0*(ds+(dy*du - piby2_tail)));
      } else
	{
	  float c, s1;
	  unsigned int us;
	  GET_BITS_SP32(s, us);
	  PUT_BITS_SP32(0xffff0000 & us, s1);
	  c = (r-s1*s1)/(s+s1);
          return 2.0F*s1 + (2.0F*c+2.0F*y*u);
	}
    }
  else
  {
    double dx = (double)x, du = (double)u;
    return (float)(piby2_head - (dx - (piby2_tail - dx*du)));
  }
}



/* ---- aocl-libm-ose src/ref/atanf.c ---- */



float ALM_PROTO_REF(atanf)(float fx)
{

  /* Some constants and split constants. */

  static double piby2 = 1.5707963267948966e+00; /* 0x3ff921fb54442d18 */

  double c, v, s, q, z;
  unsigned int xnan,fux,faux;
  unsigned long long ux, aux, xneg;
  double x;
   GET_BITS_SP32(fx, fux);
   faux = fux & ~SIGNBIT_SP32;

    xnan = (faux > PINFBITPATT_SP32);

      if (xnan)
        {
          /* x is NaN */
#ifdef WINDOWS
		return  __alm_handle_errorf(fux|0x00400000, 0);
#else
          return fx + fx; /* Raise invalid if it's a signalling NaN */
#endif
        }



	  x = fx;
    GET_BITS_DP64(x, ux);
  /* Find properties of argument fx. */


  aux = ux & ~SIGNBIT_DP64;
  xneg = ux & SIGNBIT_DP64;

  v = x;
  if (xneg) v = -x;

  /* Argument reduction to range [-7/16,7/16] */

  if (aux < 0x3ec0000000000000) /* v < 2.0^(-19) */
    {
      /* x is a good approximation to atan(x) */
      if (aux == 0x0000000000000000)
        return fx;
      else
#ifdef WINDOWS
        return fx ; //valf_with_flags(fx, AMD_F_INEXACT);
#else
	return  __alm_handle_errorf(fux, AMD_F_UNDERFLOW|AMD_F_INEXACT);

#endif
    }
  else if (aux < 0x3fdc000000000000) /* v < 7./16. */
    {
      x = v;
      c = 0.0;
    }
  else if (aux < 0x3fe6000000000000) /* v < 11./16. */
    {
      x = (2.0*v-1.0)/(2.0+v);
      /* c = arctan(0.5) */
      c = 4.63647609000806093515e-01; /* 0x3fddac670561bb4f */
    }
  else if (aux < 0x3ff3000000000000) /* v < 19./16. */
    {
      x = (v-1.0)/(1.0+v);
      /* c = arctan(1.) */
      c = 7.85398163397448278999e-01; /* 0x3fe921fb54442d18 */
    }
  else if (aux < 0x4003800000000000) /* v < 39./16. */
    {
      x = (v-1.5)/(1.0+1.5*v);
      /* c = arctan(1.5) */
      c = 9.82793723247329054082e-01; /* 0x3fef730bd281f69b */
    }
  else
    {
    if (aux > 0x4190000000000000)
	{ /* abs(x) > 2^26 => arctan(1/x) is
	     insignificant compared to piby2 */
	  if (xneg)
            return (float)-piby2 ; //valf_with_flags((float)-piby2, AMD_F_INEXACT);
	  else
            return (float)piby2;//valf_with_flags((float)piby2, AMD_F_INEXACT);
	}

      x = -1.0/v;
      /* c = arctan(infinity) */
      c = 1.57079632679489655800e+00; /* 0x3ff921fb54442d18 */
    }

  /* Core approximation: Remez(2,2) on [-7/16,7/16] */

  s = x*x;
  q = x*s*
    (0.296528598819239217902158651186e0 +
     (0.192324546402108583211697690500e0 +
       0.470677934286149214138357545549e-2*s)*s)/
    (0.889585796862432286486651434570e0 +
     (0.111072499995399550138837673349e1 +
       0.299309699959659728404442796915e0*s)*s);

  z = c - (q - x);

  if (xneg) z = -z;
  return (float)z;
}



/* ---- aocl-libm-ose src/ref/atan2f.c ---- */


float ALM_PROTO_REF(atan2f)(float fy, float fx)
{
  /* Array atan_jby256 contains precomputed values of atan(j/256),
     for j = 16, 17, ..., 256. */

  static const double atan_jby256[  241] = {
    6.24188099959573430842e-02,  /* 0x3faff55bb72cfde9 */
    6.63088949198234745008e-02,  /* 0x3fb0f99ea71d52a6 */
    7.01969710718705064423e-02,  /* 0x3fb1f86dbf082d58 */
    7.40829225490337306415e-02,  /* 0x3fb2f719318a4a9a */
    7.79666338315423007588e-02,  /* 0x3fb3f59f0e7c559d */
    8.18479898030765457007e-02,  /* 0x3fb4f3fd677292fb */
    8.57268757707448092464e-02,  /* 0x3fb5f2324fd2d7b2 */
    8.96031774848717321724e-02,  /* 0x3fb6f03bdcea4b0c */
    9.34767811585894559112e-02,  /* 0x3fb7ee182602f10e */
    9.73475734872236708739e-02,  /* 0x3fb8ebc54478fb28 */
    1.01215441667466668485e-01,  /* 0x3fb9e94153cfdcf1 */
    1.05080273416329528224e-01,  /* 0x3fbae68a71c722b8 */
    1.08941956989865793015e-01,  /* 0x3fbbe39ebe6f07c3 */
    1.12800381201659388752e-01,  /* 0x3fbce07c5c3cca32 */
    1.16655435441069349478e-01,  /* 0x3fbddd21701eba6e */
    1.20507009691224548087e-01,  /* 0x3fbed98c2190043a */
    1.24354994546761424279e-01,  /* 0x3fbfd5ba9aac2f6d */
    1.28199281231298117811e-01,  /* 0x3fc068d584212b3d */
    1.32039761614638734288e-01,  /* 0x3fc0e6adccf40881 */
    1.35876328229701304195e-01,  /* 0x3fc1646541060850 */
    1.39708874289163620386e-01,  /* 0x3fc1e1fafb043726 */
    1.43537293701821222491e-01,  /* 0x3fc25f6e171a535c */
    1.47361481088651630200e-01,  /* 0x3fc2dcbdb2fba1ff */
    1.51181331798580037562e-01,  /* 0x3fc359e8edeb99a3 */
    1.54996741923940972718e-01,  /* 0x3fc3d6eee8c6626c */
    1.58807608315631065832e-01,  /* 0x3fc453cec6092a9e */
    1.62613828597948567589e-01,  /* 0x3fc4d087a9da4f17 */
    1.66415301183114927586e-01,  /* 0x3fc54d18ba11570a */
    1.70211925285474380276e-01,  /* 0x3fc5c9811e3ec269 */
    1.74003600935367680469e-01,  /* 0x3fc645bfffb3aa73 */
    1.77790228992676047071e-01,  /* 0x3fc6c1d4898933d8 */
    1.81571711160032150945e-01,  /* 0x3fc73dbde8a7d201 */
    1.85347949995694760705e-01,  /* 0x3fc7b97b4bce5b02 */
    1.89118848926083965578e-01,  /* 0x3fc8350be398ebc7 */
    1.92884312257974643856e-01,  /* 0x3fc8b06ee2879c28 */
    1.96644245190344985064e-01,  /* 0x3fc92ba37d050271 */
    2.00398553825878511514e-01,  /* 0x3fc9a6a8e96c8626 */
    2.04147145182116990236e-01,  /* 0x3fca217e601081a5 */
    2.07889927202262986272e-01,  /* 0x3fca9c231b403279 */
    2.11626808765629753628e-01,  /* 0x3fcb1696574d780b */
    2.15357699697738047551e-01,  /* 0x3fcb90d7529260a2 */
    2.19082510780057748701e-01,  /* 0x3fcc0ae54d768466 */
    2.22801153759394493514e-01,  /* 0x3fcc84bf8a742e6d */
    2.26513541356919617664e-01,  /* 0x3fccfe654e1d5395 */
    2.30219587276843717927e-01,  /* 0x3fcd77d5df205736 */
    2.33919206214733416127e-01,  /* 0x3fcdf110864c9d9d */
    2.37612313865471241892e-01,  /* 0x3fce6a148e96ec4d */
    2.41298826930858800743e-01,  /* 0x3fcee2e1451d980c */
    2.44978663126864143473e-01,  /* 0x3fcf5b75f92c80dd */
    2.48651741190513253521e-01,  /* 0x3fcfd3d1fc40dbe4 */
    2.52317980886427151166e-01,  /* 0x3fd025fa510665b5 */
    2.55977303013005474952e-01,  /* 0x3fd061eea03d6290 */
    2.59629629408257511791e-01,  /* 0x3fd09dc597d86362 */
    2.63274882955282396590e-01,  /* 0x3fd0d97ee509acb3 */
    2.66912987587400396539e-01,  /* 0x3fd1151a362431c9 */
    2.70543868292936529052e-01,  /* 0x3fd150973a9ce546 */
    2.74167451119658789338e-01,  /* 0x3fd18bf5a30bf178 */
    2.77783663178873208022e-01,  /* 0x3fd1c735212dd883 */
    2.81392432649178403370e-01,  /* 0x3fd2025567e47c95 */
    2.84993688779881237938e-01,  /* 0x3fd23d562b381041 */
    2.88587361894077354396e-01,  /* 0x3fd278372057ef45 */
    2.92173383391398755471e-01,  /* 0x3fd2b2f7fd9b5fe2 */
    2.95751685750431536626e-01,  /* 0x3fd2ed987a823cfe */
    2.99322202530807379706e-01,  /* 0x3fd328184fb58951 */
    3.02884868374971361060e-01,  /* 0x3fd362773707ebcb */
    3.06439619009630070945e-01,  /* 0x3fd39cb4eb76157b */
    3.09986391246883430384e-01,  /* 0x3fd3d6d129271134 */
    3.13525122985043869228e-01,  /* 0x3fd410cbad6c7d32 */
    3.17055753209146973237e-01,  /* 0x3fd44aa436c2af09 */
    3.20578221991156986359e-01,  /* 0x3fd4845a84d0c21b */
    3.24092470489871664618e-01,  /* 0x3fd4bdee586890e6 */
    3.27598440950530811477e-01,  /* 0x3fd4f75f73869978 */
    3.31096076704132047386e-01,  /* 0x3fd530ad9951cd49 */
    3.34585322166458920545e-01,  /* 0x3fd569d88e1b4cd7 */
    3.38066122836825466713e-01,  /* 0x3fd5a2e0175e0f4e */
    3.41538425296541714449e-01,  /* 0x3fd5dbc3fbbe768d */
    3.45002177207105076295e-01,  /* 0x3fd614840309cfe1 */
    3.48457327308122011278e-01,  /* 0x3fd64d1ff635c1c5 */
    3.51903825414964732676e-01,  /* 0x3fd685979f5fa6fd */
    3.55341622416168290144e-01,  /* 0x3fd6bdeac9cbd76c */
    3.58770670270572189509e-01,  /* 0x3fd6f61941e4def0 */
    3.62190922004212156882e-01,  /* 0x3fd72e22d53aa2a9 */
    3.65602331706966821034e-01,  /* 0x3fd7660752817501 */
    3.69004854528964421068e-01,  /* 0x3fd79dc6899118d1 */
    3.72398446676754202311e-01,  /* 0x3fd7d5604b63b3f7 */
    3.75783065409248884237e-01,  /* 0x3fd80cd46a14b1d0 */
    3.79158669033441808605e-01,  /* 0x3fd84422b8df95d7 */
    3.82525216899905096124e-01,  /* 0x3fd87b4b0c1ebedb */
    3.85882669398073752109e-01,  /* 0x3fd8b24d394a1b25 */
    3.89230987951320717144e-01,  /* 0x3fd8e92916f5cde8 */
    3.92570135011828580396e-01,  /* 0x3fd91fde7cd0c662 */
    3.95900074055262896078e-01,  /* 0x3fd9566d43a34907 */
    3.99220769575252543149e-01,  /* 0x3fd98cd5454d6b18 */
    4.02532187077682512832e-01,  /* 0x3fd9c3165cc58107 */
    4.05834293074804064450e-01,  /* 0x3fd9f93066168001 */
    4.09127055079168300278e-01,  /* 0x3fda2f233e5e530b */
    4.12410441597387267265e-01,  /* 0x3fda64eec3cc23fc */
    4.15684422123729413467e-01,  /* 0x3fda9a92d59e98cf */
    4.18948967133552840902e-01,  /* 0x3fdad00f5422058b */
    4.22204048076583571270e-01,  /* 0x3fdb056420ae9343 */
    4.25449637370042266227e-01,  /* 0x3fdb3a911da65c6c */
    4.28685708391625730496e-01,  /* 0x3fdb6f962e737efb */
    4.31912235472348193799e-01,  /* 0x3fdba473378624a5 */
    4.35129193889246812521e-01,  /* 0x3fdbd9281e528191 */
    4.38336559857957774877e-01,  /* 0x3fdc0db4c94ec9ef */
    4.41534310525166673322e-01,  /* 0x3fdc42191ff11eb6 */
    4.44722423960939305942e-01,  /* 0x3fdc76550aad71f8 */
    4.47900879150937292206e-01,  /* 0x3fdcaa6872f3631b */
    4.51069655988523443568e-01,  /* 0x3fdcde53432c1350 */
    4.54228735266762495559e-01,  /* 0x3fdd121566b7f2ad */
    4.57378098670320809571e-01,  /* 0x3fdd45aec9ec862b */
    4.60517728767271039558e-01,  /* 0x3fdd791f5a1226f4 */
    4.63647609000806093515e-01,  /* 0x3fddac670561bb4f */
    4.66767723680866497560e-01,  /* 0x3fdddf85bb026974 */
    4.69878057975686880265e-01,  /* 0x3fde127b6b0744af */
    4.72978597903265574054e-01,  /* 0x3fde4548066cf51a */
    4.76069330322761219421e-01,  /* 0x3fde77eb7f175a34 */
    4.79150242925822533735e-01,  /* 0x3fdeaa65c7cf28c4 */
    4.82221324227853687105e-01,  /* 0x3fdedcb6d43f8434 */
    4.85282563559221225002e-01,  /* 0x3fdf0ede98f393cf */
    4.88333951056405479729e-01,  /* 0x3fdf40dd0b541417 */
    4.91375477653101910835e-01,  /* 0x3fdf72b221a4e495 */
    4.94407135071275316562e-01,  /* 0x3fdfa45dd3029258 */
    4.97428915812172245392e-01,  /* 0x3fdfd5e0175fdf83 */
    5.00440813147294050189e-01,  /* 0x3fe0039c73c1a40b */
    5.03442821109336358099e-01,  /* 0x3fe01c341e82422d */
    5.06434934483096732549e-01,  /* 0x3fe034b709250488 */
    5.09417148796356245022e-01,  /* 0x3fe04d25314342e5 */
    5.12389460310737621107e-01,  /* 0x3fe0657e94db30cf */
    5.15351866012543347040e-01,  /* 0x3fe07dc3324e9b38 */
    5.18304363603577900044e-01,  /* 0x3fe095f30861a58f */
    5.21246951491958210312e-01,  /* 0x3fe0ae0e1639866c */
    5.24179628782913242802e-01,  /* 0x3fe0c6145b5b43da */
    5.27102395269579471204e-01,  /* 0x3fe0de05d7aa6f7c */
    5.30015251423793132268e-01,  /* 0x3fe0f5e28b67e295 */
    5.32918198386882147055e-01,  /* 0x3fe10daa77307a0d */
    5.35811237960463593311e-01,  /* 0x3fe1255d9bfbd2a8 */
    5.38694372597246617929e-01,  /* 0x3fe13cfbfb1b056e */
    5.41567605391844897333e-01,  /* 0x3fe1548596376469 */
    5.44430940071603086672e-01,  /* 0x3fe16bfa6f5137e1 */
    5.47284380987436924748e-01,  /* 0x3fe1835a88be7c13 */
    5.50127933104692989907e-01,  /* 0x3fe19aa5e5299f99 */
    5.52961601994028217888e-01,  /* 0x3fe1b1dc87904284 */
    5.55785393822313511514e-01,  /* 0x3fe1c8fe7341f64f */
    5.58599315343562330405e-01,  /* 0x3fe1e00babdefeb3 */
    5.61403373889889367732e-01,  /* 0x3fe1f7043557138a */
    5.64197577362497537656e-01,  /* 0x3fe20de813e823b1 */
    5.66981934222700489912e-01,  /* 0x3fe224b74c1d192a */
    5.69756453482978431069e-01,  /* 0x3fe23b71e2cc9e6a */
    5.72521144698072359525e-01,  /* 0x3fe25217dd17e501 */
    5.75276017956117824426e-01,  /* 0x3fe268a940696da6 */
    5.78021083869819540801e-01,  /* 0x3fe27f261273d1b3 */
    5.80756353567670302596e-01,  /* 0x3fe2958e59308e30 */
    5.83481838685214859730e-01,  /* 0x3fe2abe21aded073 */
    5.86197551356360535557e-01,  /* 0x3fe2c2215e024465 */
    5.88903504204738026395e-01,  /* 0x3fe2d84c2961e48b */
    5.91599710335111383941e-01,  /* 0x3fe2ee628406cbca */
    5.94286183324841177367e-01,  /* 0x3fe30464753b090a */
    5.96962937215401501234e-01,  /* 0x3fe31a52048874be */
    5.99629986503951384336e-01,  /* 0x3fe3302b39b78856 */
    6.02287346134964152178e-01,  /* 0x3fe345f01cce37bb */
    6.04935031491913965951e-01,  /* 0x3fe35ba0b60eccce */
    6.07573058389022313541e-01,  /* 0x3fe3713d0df6c503 */
    6.10201443063065118722e-01,  /* 0x3fe386c52d3db11e */
    6.12820202165241245673e-01,  /* 0x3fe39c391cd41719 */
    6.15429352753104952356e-01,  /* 0x3fe3b198e5e2564a */
    6.18028912282561737612e-01,  /* 0x3fe3c6e491c78dc4 */
    6.20618898599929469384e-01,  /* 0x3fe3dc1c2a188504 */
    6.23199329934065904268e-01,  /* 0x3fe3f13fb89e96f4 */
    6.25770224888563042498e-01,  /* 0x3fe4064f47569f48 */
    6.28331602434009650615e-01,  /* 0x3fe41b4ae06fea41 */
    6.30883481900321840818e-01,  /* 0x3fe430328e4b26d5 */
    6.33425882969144482537e-01,  /* 0x3fe445065b795b55 */
    6.35958825666321447834e-01,  /* 0x3fe459c652badc7f */
    6.38482330354437466191e-01,  /* 0x3fe46e727efe4715 */
    6.40996417725432032775e-01,  /* 0x3fe4830aeb5f7bfd */
    6.43501108793284370968e-01,  /* 0x3fe4978fa3269ee1 */
    6.45996424886771558604e-01,  /* 0x3fe4ac00b1c71762 */
    6.48482387642300484032e-01,  /* 0x3fe4c05e22de94e4 */
    6.50959018996812410762e-01,  /* 0x3fe4d4a8023414e8 */
    6.53426341180761927063e-01,  /* 0x3fe4e8de5bb6ec04 */
    6.55884376711170835605e-01,  /* 0x3fe4fd013b7dd17e */
    6.58333148384755983962e-01,  /* 0x3fe51110adc5ed81 */
    6.60772679271132590273e-01,  /* 0x3fe5250cbef1e9fa */
    6.63202992706093175102e-01,  /* 0x3fe538f57b89061e */
    6.65624112284960989250e-01,  /* 0x3fe54ccaf0362c8f */
    6.68036061856020157990e-01,  /* 0x3fe5608d29c70c34 */
    6.70438865514021320458e-01,  /* 0x3fe5743c352b33b9 */
    6.72832547593763097282e-01,  /* 0x3fe587d81f732fba */
    6.75217132663749830535e-01,  /* 0x3fe59b60f5cfab9d */
    6.77592645519925151909e-01,  /* 0x3fe5aed6c5909517 */
    6.79959111179481823228e-01,  /* 0x3fe5c2399c244260 */
    6.82316554874748071313e-01,  /* 0x3fe5d58987169b18 */
    6.84665002047148862907e-01,  /* 0x3fe5e8c6941043cf */
    6.87004478341244895212e-01,  /* 0x3fe5fbf0d0d5cc49 */
    6.89335009598845749323e-01,  /* 0x3fe60f084b46e05e */
    6.91656621853199760075e-01,  /* 0x3fe6220d115d7b8d */
    6.93969341323259825138e-01,  /* 0x3fe634ff312d1f3b */
    6.96273194408023488045e-01,  /* 0x3fe647deb8e20b8f */
    6.98568207680949848637e-01,  /* 0x3fe65aabb6c07b02 */
    7.00854407884450081312e-01,  /* 0x3fe66d663923e086 */
    7.03131821924453670469e-01,  /* 0x3fe6800e4e7e2857 */
    7.05400476865049030906e-01,  /* 0x3fe692a40556fb6a */
    7.07660399923197958039e-01,  /* 0x3fe6a5276c4b0575 */
    7.09911618463524796141e-01,  /* 0x3fe6b798920b3d98 */
    7.12154159993178659249e-01,  /* 0x3fe6c9f7855c3198 */
    7.14388052156768926793e-01,  /* 0x3fe6dc44551553ae */
    7.16613322731374569052e-01,  /* 0x3fe6ee7f10204aef */
    7.18829999621624415873e-01,  /* 0x3fe700a7c5784633 */
    7.21038110854851588272e-01,  /* 0x3fe712be84295198 */
    7.23237684576317874097e-01,  /* 0x3fe724c35b4fae7b */
    7.25428749044510712274e-01,  /* 0x3fe736b65a172dff */
    7.27611332626510676214e-01,  /* 0x3fe748978fba8e0f */
    7.29785463793429123314e-01,  /* 0x3fe75a670b82d8d8 */
    7.31951171115916565668e-01,  /* 0x3fe76c24dcc6c6c0 */
    7.34108483259739652560e-01,  /* 0x3fe77dd112ea22c7 */
    7.36257428981428097003e-01,  /* 0x3fe78f6bbd5d315e */
    7.38398037123989547936e-01,  /* 0x3fe7a0f4eb9c19a2 */
    7.40530336612692630105e-01,  /* 0x3fe7b26cad2e50fd */
    7.42654356450917929600e-01,  /* 0x3fe7c3d311a6092b */
    7.44770125716075148681e-01,  /* 0x3fe7d528289fa093 */
    7.46877673555587429099e-01,  /* 0x3fe7e66c01c114fd */
    7.48977029182941400620e-01,  /* 0x3fe7f79eacb97898 */
    7.51068221873802288613e-01,  /* 0x3fe808c03940694a */
    7.53151280962194302759e-01,  /* 0x3fe819d0b7158a4c */
    7.55226235836744863583e-01,  /* 0x3fe82ad036000005 */
    7.57293115936992444759e-01,  /* 0x3fe83bbec5cdee22 */
    7.59351950749757920178e-01,  /* 0x3fe84c9c7653f7ea */
    7.61402769805578416573e-01,  /* 0x3fe85d69576cc2c5 */
    7.63445602675201784315e-01,  /* 0x3fe86e2578f87ae5 */
    7.65480478966144461950e-01,  /* 0x3fe87ed0eadc5a2a */
    7.67507428319308182552e-01,  /* 0x3fe88f6bbd023118 */
    7.69526480405658186434e-01,  /* 0x3fe89ff5ff57f1f7 */
    7.71537664922959498526e-01,  /* 0x3fe8b06fc1cf3dfe */
    7.73541011592573490852e-01,  /* 0x3fe8c0d9145cf49d */
    7.75536550156311621507e-01,  /* 0x3fe8d13206f8c4ca */
    7.77524310373347682379e-01,  /* 0x3fe8e17aa99cc05d */
    7.79504322017186335181e-01,  /* 0x3fe8f1b30c44f167 */
    7.81476614872688268854e-01,  /* 0x3fe901db3eeef187 */
    7.83441218733151756304e-01,  /* 0x3fe911f35199833b */
    7.85398163397448278999e-01}; /* 0x3fe921fb54442d18 */

  /* Some constants. */

  static double pi = 3.1415926535897932e+00, /* 0x400921fb54442d18 */
             piby2 = 1.5707963267948966e+00, /* 0x3ff921fb54442d18 */
             piby4 = 7.8539816339744831e-01, /* 0x3fe921fb54442d18 */
       three_piby4 = 2.3561944901923449e+00; /* 0x4002d97c7f3321d2 */

  double u, v, vbyu, q, s, uu, r;
  unsigned int swap_vu, index, xzero, yzero, xnan, ynan, xinf, yinf;
  int xexp, yexp, diffexp;
  uint64_t uy;

  double x;
  double y;
  unsigned int ufx,aufx,xneg;
  unsigned int ufy,aufy,yneg;

  GET_BITS_SP32(fy, ufy);
  GET_BITS_SP32(fx, ufx);

  xneg = ufx & SIGNBIT_SP32;
  yneg = ufy & SIGNBIT_SP32;
  aufx = ufx & ~SIGNBIT_SP32;
  aufy = ufy & ~SIGNBIT_SP32;

  xzero = (aufx == 0);
  yzero = (aufy == 0);
  xnan = (aufx > PINFBITPATT_SP32);
  ynan = (aufy > PINFBITPATT_SP32);
  xinf = (aufx == PINFBITPATT_SP32);
  yinf = (aufy == PINFBITPATT_SP32);

  xexp = (int)((ufx & EXPBITS_SP32) >> EXPSHIFTBITS_SP32);
  yexp = (int)((ufy & EXPBITS_SP32) >> EXPSHIFTBITS_SP32);


  diffexp = yexp - xexp;


  /* Special cases */

  /* IEEE 754: If either x or y is NaN, return NaN */
  if (xnan || ynan) {
#ifdef WINDOWS
    /*
     * Branchless sNaN vs qNaN handling:
     * sNaN (quiet bit = 0): raise FE_INVALID
     * qNaN (quiet bit = 1): no exception
     */
    int is_snan = (xnan && !(aufx & QNAN_MASK_32)) ||
                  (ynan && !(aufy & QNAN_MASK_32));
    return __alm_handle_errorf(
        (xnan ? ufx : ufy) | QNAN_MASK_32,
        is_snan ? AMD_F_INVALID : AMD_F_NONE
    );
#else
    return fx + fy; /* Propagate NaN, raise invalid for sNaN */
#endif
  }
  else if (yzero)
    { /* Zero y gives +-0 for positive x
         and +-pi for negative x */
      if (xneg)
    {
      if (yneg) return (float)-pi; // return valf_with_flags((float)-pi, AMD_F_INEXACT);
          else return (float)pi; // return valf_with_flags((float)pi, AMD_F_INEXACT);
    }
      else return fy;
    }
  else if (xzero)
    { /* Zero x gives +- pi/2
         depending on sign of y */
      if (yneg) return (float)-piby2; //return valf_with_flags((float)-piby2, AMD_F_INEXACT);
      else return (float)piby2; //valf_with_flags((float)piby2, AMD_F_INEXACT);
    }


   x = fx;
   y = fy;

  /* Find properties of arguments x and y. */






  if (diffexp > 26)
    { /* abs(y)/abs(x) > 2^26 => arctan(x/y)
         is insignificant compared to piby2 */
      if (yneg) return (float)-piby2; //return valf_with_flags((float)-piby2, AMD_F_INEXACT);
      else return (float)piby2; //return valf_with_flags((float)piby2, AMD_F_INEXACT);
    }
  else if (diffexp < -13 && (!xneg))
    { /* x positive and dominant over y by a factor of 2^13.
         In this case atan(y/x) is y/x to machine accuracy. */

      if (diffexp < -150) /* Result underflows */
        {
          /* Raise FE_UNDERFLOW on all platforms */
          if (yneg)
            return __alm_handle_errorf(NEG_ZERO_F32, AMD_F_INEXACT | AMD_F_UNDERFLOW);
          else
            return __alm_handle_errorf(POS_ZERO_F32, AMD_F_INEXACT | AMD_F_UNDERFLOW);
        }
      else
        {
          if (diffexp < -126)
            {
              /* Result will likely be denormalized */
              y = scaleDouble_1(y, 100);
              y /= x;
              /* Now y is 2^100 times the true result. Scale it back down. */
              GET_BITS_DP64(y, uy);
              scaleDownDouble(uy, 100, &uy);
              PUT_BITS_DP64(uy, y);
              return (float)y; //return valf_with_flags((float)y, AMD_F_INEXACT | AMD_F_UNDERFLOW);
             }
          else
            return (float)(y / x);
        }
    }
  else if (diffexp < -26 && xneg)
    { /* abs(x)/abs(y) > 2^56 and x < 0 => arctan(y/x)
         is insignificant compared to pi */
    if (yneg) return (float)-pi; //  return valf_with_flags((float)-pi, AMD_F_INEXACT);
    else return (float)pi;// return valf_with_flags((float)pi, AMD_F_INEXACT);
    }
  else if (yinf && xinf)
    { /* If abs(x) and abs(y) are both infinity
         return +-pi/4 or +- 3pi/4 according to
         signs.  */
    if (xneg)
      {
      if (yneg) return (float)-three_piby4; // return valf_with_flags((float)-three_piby4, AMD_F_INEXACT);
      else return (float)three_piby4; //return valf_with_flags((float)three_piby4, AMD_F_INEXACT);
      }
    else
      {
      if (yneg) return (float)-piby4; //return valf_with_flags((float)-piby4, AMD_F_INEXACT);
      else return (float)piby4; //return valf_with_flags((float)piby4, AMD_F_INEXACT);
      }
    }

  /* General case: take absolute values of arguments */

  u = x; v = y;
  if (xneg) u = -x;
  if (yneg) v = -y;

  /* Swap u and v if necessary to obtain 0 < v < u. Compute v/u. */

  swap_vu = (u < v);
  if (swap_vu) { uu = u; u = v; v = uu; }
  vbyu = v/u;

  if (vbyu > 0.0625)
    { /* General values of v/u. Use a look-up
         table and series expansion. */

      index = (unsigned int)(256*vbyu + 0.5);
      r = (256*v-index*u)/(256*u+index*v);

      /* Polynomial approximation to atan(vbyu) */

      s = r*r;
      q = atan_jby256[index-16] + r - r*s*0.33333333333224095522;
    }
  else if (vbyu < 1.e-4)
    { /* v/u is small enough that atan(v/u) = v/u */
      q = vbyu;
    }
  else /* vbyu <= 0.0625 */
    {
      /* Small values of v/u. Use a series expansion */

      s  = vbyu*vbyu;
      q = vbyu -
    vbyu*s*(0.33333333333333170500 -
        s*(0.19999999999393223405 -
           s*0.14285713561807169030));
    }

  /* Tidy-up according to which quadrant the arguments lie in */

  if (swap_vu) {q = piby2 - q;}
  if (xneg) {q = pi - q;}
  if (yneg) q = - q;
  return (float)q;
}


/* ---- aocl-libm-ose src/ref/sinhf.c ---- */



#ifdef WINDOWS
#endif

float ALM_PROTO_REF(sinhf)(float fx)
{
  /*
    After dealing with special cases the computation is split into
    regions as follows:

    abs(x) >= max_sinh_arg:
    sinh(x) = sign(x)*Inf

    abs(x) >= small_threshold:
    sinh(x) = sign(x)*exp(abs(x))/2 computed using the
    splitexp and scaleDouble functions as for exp_amd().

    abs(x) < small_threshold:
    compute p = exp(y) - 1 and then z = 0.5*(p+(p/(p+1.0)))
    sinh(x) is then sign(x)*z.                             */

  static const double
    /* The max argument of sinhf, but stored as a double */
    max_sinh_arg = 8.94159862922329438106e+01, /* 0x40565a9f84f82e63 */
    thirtytwo_by_log2 = 4.61662413084468283841e+01, /* 0x40471547652b82fe */
    log2_by_32_lead = 2.16608493356034159660e-02, /* 0x3f962e42fe000000 */
    log2_by_32_tail = 5.68948749532545630390e-11, /* 0x3dcf473de6af278e */
    small_threshold = 8*BASEDIGITS_DP64*0.30102999566398119521373889;
  /* (8*BASEDIGITS_DP64*log10of2) ' exp(-x) insignificant c.f. exp(x) */

  /* Tabulated values of sinh(i) and cosh(i) for i = 0,...,36. */

  static const double sinh_lead[37] = {
    0.00000000000000000000e+00,  /* 0x0000000000000000 */
    1.17520119364380137839e+00,  /* 0x3ff2cd9fc44eb982 */
    3.62686040784701857476e+00,  /* 0x400d03cf63b6e19f */
    1.00178749274099008204e+01,  /* 0x40240926e70949ad */
    2.72899171971277496596e+01,  /* 0x403b4a3803703630 */
    7.42032105777887522891e+01,  /* 0x40528d0166f07374 */
    2.01713157370279219549e+02,  /* 0x406936d22f67c805 */
    5.48316123273246489589e+02,  /* 0x408122876ba380c9 */
    1.49047882578955000099e+03,  /* 0x409749ea514eca65 */
    4.05154190208278987484e+03,  /* 0x40afa7157430966f */
    1.10132328747033916443e+04,  /* 0x40c5829dced69991 */
    2.99370708492480553105e+04,  /* 0x40dd3c4488cb48d6 */
    8.13773957064298447222e+04,  /* 0x40f3de1654d043f0 */
    2.21206696003330085659e+05,  /* 0x410b00b5916a31a5 */
    6.01302142081972560845e+05,  /* 0x412259ac48bef7e3 */
    1.63450868623590236530e+06,  /* 0x4138f0ccafad27f6 */
    4.44305526025387924165e+06,  /* 0x4150f2ebd0a7ffe3 */
    1.20774763767876271158e+07,  /* 0x416709348c0ea4ed */
    3.28299845686652474105e+07,  /* 0x417f4f22091940bb */
    8.92411504815936237574e+07,  /* 0x419546d8f9ed26e1 */
    2.42582597704895108938e+08,  /* 0x41aceb088b68e803 */
    6.59407867241607308388e+08,  /* 0x41c3a6e1fd9eecfd */
    1.79245642306579566002e+09,  /* 0x41dab5adb9c435ff */
    4.87240172312445068359e+09,  /* 0x41f226af33b1fdc0 */
    1.32445610649217357635e+10,  /* 0x4208ab7fb5475fb7 */
    3.60024496686929321289e+10,  /* 0x4220c3d3920962c8 */
    9.78648047144193725586e+10,  /* 0x4236c932696a6b5c */
    2.66024120300899291992e+11,  /* 0x424ef822f7f6731c */
    7.23128532145737548828e+11,  /* 0x42650bba3796379a */
    1.96566714857202099609e+12,  /* 0x427c9aae4631c056 */
    5.34323729076223046875e+12,  /* 0x429370470aec28ec */
    1.45244248326237109375e+13,  /* 0x42aa6b765d8cdf6c */
    3.94814800913403437500e+13,  /* 0x42c1f43fcc4b662c */
    1.07321789892958031250e+14,  /* 0x42d866f34a725782 */
    2.91730871263727437500e+14,  /* 0x42f0953e2f3a1ef7 */
    7.93006726156715250000e+14,  /* 0x430689e221bc8d5a */
    2.15561577355759750000e+15}; /* 0x431ea215a1d20d76 */

  static const double cosh_lead[37] = {
    1.00000000000000000000e+00,  /* 0x3ff0000000000000 */
    1.54308063481524371241e+00,  /* 0x3ff8b07551d9f550 */
    3.76219569108363138810e+00,  /* 0x400e18fa0df2d9bc */
    1.00676619957777653269e+01,  /* 0x402422a497d6185e */
    2.73082328360164865444e+01,  /* 0x403b4ee858de3e80 */
    7.42099485247878334349e+01,  /* 0x40528d6fcbeff3a9 */
    2.01715636122455890700e+02,  /* 0x406936e67db9b919 */
    5.48317035155212010977e+02,  /* 0x4081228949ba3a8b */
    1.49047916125217807348e+03,  /* 0x409749eaa93f4e76 */
    4.05154202549259389343e+03,  /* 0x40afa715845d8894 */
    1.10132329201033226127e+04,  /* 0x40c5829dd053712d */
    2.99370708659497577173e+04,  /* 0x40dd3c4489115627 */
    8.13773957125740562333e+04,  /* 0x40f3de1654d6b543 */
    2.21206696005590405548e+05,  /* 0x410b00b5916b6105 */
    6.01302142082804115489e+05,  /* 0x412259ac48bf13ca */
    1.63450868623620807193e+06,  /* 0x4138f0ccafad2d17 */
    4.44305526025399193168e+06,  /* 0x4150f2ebd0a8005c */
    1.20774763767876680940e+07,  /* 0x416709348c0ea503 */
    3.28299845686652623117e+07,  /* 0x417f4f22091940bf */
    8.92411504815936237574e+07,  /* 0x419546d8f9ed26e1 */
    2.42582597704895138741e+08,  /* 0x41aceb088b68e804 */
    6.59407867241607308388e+08,  /* 0x41c3a6e1fd9eecfd */
    1.79245642306579566002e+09,  /* 0x41dab5adb9c435ff */
    4.87240172312445068359e+09,  /* 0x41f226af33b1fdc0 */
    1.32445610649217357635e+10,  /* 0x4208ab7fb5475fb7 */
    3.60024496686929321289e+10,  /* 0x4220c3d3920962c8 */
    9.78648047144193725586e+10,  /* 0x4236c932696a6b5c */
    2.66024120300899291992e+11,  /* 0x424ef822f7f6731c */
    7.23128532145737548828e+11,  /* 0x42650bba3796379a */
    1.96566714857202099609e+12,  /* 0x427c9aae4631c056 */
    5.34323729076223046875e+12,  /* 0x429370470aec28ec */
    1.45244248326237109375e+13,  /* 0x42aa6b765d8cdf6c */
    3.94814800913403437500e+13,  /* 0x42c1f43fcc4b662c */
    1.07321789892958031250e+14,  /* 0x42d866f34a725782 */
    2.91730871263727437500e+14,  /* 0x42f0953e2f3a1ef7 */
    7.93006726156715250000e+14,  /* 0x430689e221bc8d5a */
    2.15561577355759750000e+15}; /* 0x431ea215a1d20d76 */

  unsigned long long  aux, xneg;
  unsigned int ux;
  double y, z, z1, z2;
  int m;

  /* Special cases */

  GET_BITS_SP32(fx, ux);
  aux = ux & ~SIGNBIT_SP32;
  
  if (aux < 0x38800000) /* |x| small enough that sinh(x) = x */
    {
        if (aux == 0x00000000)
        {
            /* x is +/-zero. Return the same zero. */
            return fx;
        }
        else
        {
#ifdef WINDOWS
            return fx;
#else
            return __alm_handle_errorf(ux, AMD_F_INEXACT|AMD_F_UNDERFLOW);
#endif
        }
    }
  else if (aux == 0x7f800000) /* |x| is NaN or Inf */
    {
        return fx;
    }
  else if (aux > 0x7f800000)
    {
#ifdef WINDOWS
        return __alm_handle_errorf(ux|QNANBITPATT_SP32, AMD_F_NONE);
#else
        return fx+fx;
#endif
   }

  xneg = (aux != ux);

  y = fx;
  if (xneg) y = -fx;

  if (y >= max_sinh_arg)
    {
      /* Return infinity with overflow flag. */
      if (xneg)
		  return __alm_handle_errorf(NINFBITPATT_SP32, AMD_F_OVERFLOW);
      else
		  return __alm_handle_errorf(PINFBITPATT_SP32, AMD_F_OVERFLOW);
    }
  else if (y >= small_threshold)
    {
      /* In this range y is large enough so that
         the negative exponential is negligible,
         so sinh(y) is approximated by sign(x)*exp(y)/2. The
         code below is an inlined version of that from
         exp() with two changes (it operates on
         y instead of x, and the division by 2 is
         done by reducing m by 1). */

      splitexp(y, 1.0, thirtytwo_by_log2, log2_by_32_lead,
               log2_by_32_tail, &m, &z1, &z2);
      m -= 1;
      /* scaleDouble_1 is always safe because the argument x was
         float, rather than double */
      z = scaleDouble_1((z1+z2),m);
    }
  else
    {
      /* In this range we find the integer part y0 of y
         and the increment dy = y - y0. We then compute

         z = sinh(y) = sinh(y0)cosh(dy) + cosh(y0)sinh(dy)

         where sinh(y0) and cosh(y0) are tabulated above. */

      int ind;
      double dy, dy2, sdy, cdy;

      ind = (int)y;
      dy = y - ind;

      dy2 = dy*dy;

      sdy = dy + dy*dy2*(0.166666666666666667013899e0 +
			 (0.833333333333329931873097e-2 +
			  (0.198412698413242405162014e-3 +
			   (0.275573191913636406057211e-5 +
			    (0.250521176994133472333666e-7 +
			     (0.160576793121939886190847e-9 +
			      0.7746188980094184251527126e-12*dy2)*dy2)*dy2)*dy2)*dy2)*dy2);

      cdy = 1 + dy2*(0.500000000000000005911074e0 +
		     (0.416666666666660876512776e-1 +
		      (0.138888888889814854814536e-2 +
		       (0.248015872460622433115785e-4 +
			(0.275573350756016588011357e-6 +
			 (0.208744349831471353536305e-8 +
			  0.1163921388172173692062032e-10*dy2)*dy2)*dy2)*dy2)*dy2)*dy2);

      z = sinh_lead[ind]*cdy + cosh_lead[ind]*sdy;
    }

  if (xneg) z = - z;
  return (float)z;
}




/* ---- aocl-libm-ose src/ref/coshf.c ---- */



float ALM_PROTO_REF(coshf)(float fx)
{
    /*
      After dealing with special cases the computation is split into
      regions as follows:

      abs(x) >= max_cosh_arg:
      cosh(x) = sign(x)*Inf

      abs(x) >= small_threshold:
      cosh(x) = sign(x)*exp(abs(x))/2 computed using the
      splitexp and scaleDouble functions as for exp_amd().

      abs(x) < small_threshold:
      compute p = exp(y) - 1 and then z = 0.5*(p+(p/(p+1.0)))
      cosh(x) is then sign(x)*z.
    */

    static const double
        /* The max argument of coshf, but stored as a double */
        max_cosh_arg = 8.94159862922329438106e+01, /* 0x40565a9f84f82e63 */
        thirtytwo_by_log2 = 4.61662413084468283841e+01, /* 0x40471547652b82fe */
        log2_by_32_lead = 2.16608493356034159660e-02, /* 0x3f962e42fe000000 */
        log2_by_32_tail = 5.68948749532545630390e-11, /* 0x3dcf473de6af278e */

        small_threshold = 8*BASEDIGITS_DP64*0.30102999566398119521373889;
        //    small_threshold = 20.0;
    /* (8*BASEDIGITS_DP64*log10of2) ' exp(-x) insignificant c.f. exp(x) */

    /* Tabulated values of sinh(i) and cosh(i) for i = 0,...,36. */

    static const double sinh_lead[   37] = {
        0.00000000000000000000e+00,  /* 0x0000000000000000 */
        1.17520119364380137839e+00,  /* 0x3ff2cd9fc44eb982 */
        3.62686040784701857476e+00,  /* 0x400d03cf63b6e19f */
        1.00178749274099008204e+01,  /* 0x40240926e70949ad */
        2.72899171971277496596e+01,  /* 0x403b4a3803703630 */
        7.42032105777887522891e+01,  /* 0x40528d0166f07374 */
        2.01713157370279219549e+02,  /* 0x406936d22f67c805 */
        5.48316123273246489589e+02,  /* 0x408122876ba380c9 */
        1.49047882578955000099e+03,  /* 0x409749ea514eca65 */
        4.05154190208278987484e+03,  /* 0x40afa7157430966f */
        1.10132328747033916443e+04,  /* 0x40c5829dced69991 */
        2.99370708492480553105e+04,  /* 0x40dd3c4488cb48d6 */
        8.13773957064298447222e+04,  /* 0x40f3de1654d043f0 */
        2.21206696003330085659e+05,  /* 0x410b00b5916a31a5 */
        6.01302142081972560845e+05,  /* 0x412259ac48bef7e3 */
        1.63450868623590236530e+06,  /* 0x4138f0ccafad27f6 */
        4.44305526025387924165e+06,  /* 0x4150f2ebd0a7ffe3 */
        1.20774763767876271158e+07,  /* 0x416709348c0ea4ed */
        3.28299845686652474105e+07,  /* 0x417f4f22091940bb */
        8.92411504815936237574e+07,  /* 0x419546d8f9ed26e1 */
        2.42582597704895108938e+08,  /* 0x41aceb088b68e803 */
        6.59407867241607308388e+08,  /* 0x41c3a6e1fd9eecfd */
        1.79245642306579566002e+09,  /* 0x41dab5adb9c435ff */
        4.87240172312445068359e+09,  /* 0x41f226af33b1fdc0 */
        1.32445610649217357635e+10,  /* 0x4208ab7fb5475fb7 */
        3.60024496686929321289e+10,  /* 0x4220c3d3920962c8 */
        9.78648047144193725586e+10,  /* 0x4236c932696a6b5c */
        2.66024120300899291992e+11,  /* 0x424ef822f7f6731c */
        7.23128532145737548828e+11,  /* 0x42650bba3796379a */
        1.96566714857202099609e+12,  /* 0x427c9aae4631c056 */
        5.34323729076223046875e+12,  /* 0x429370470aec28ec */
        1.45244248326237109375e+13,  /* 0x42aa6b765d8cdf6c */
        3.94814800913403437500e+13,  /* 0x42c1f43fcc4b662c */
        1.07321789892958031250e+14,  /* 0x42d866f34a725782 */
        2.91730871263727437500e+14,  /* 0x42f0953e2f3a1ef7 */
        7.93006726156715250000e+14,  /* 0x430689e221bc8d5a */
        2.15561577355759750000e+15}; /* 0x431ea215a1d20d76 */

    static const double cosh_lead[   37] = {
        1.00000000000000000000e+00,  /* 0x3ff0000000000000 */
        1.54308063481524371241e+00,  /* 0x3ff8b07551d9f550 */
        3.76219569108363138810e+00,  /* 0x400e18fa0df2d9bc */
        1.00676619957777653269e+01,  /* 0x402422a497d6185e */
        2.73082328360164865444e+01,  /* 0x403b4ee858de3e80 */
        7.42099485247878334349e+01,  /* 0x40528d6fcbeff3a9 */
        2.01715636122455890700e+02,  /* 0x406936e67db9b919 */
        5.48317035155212010977e+02,  /* 0x4081228949ba3a8b */
        1.49047916125217807348e+03,  /* 0x409749eaa93f4e76 */
        4.05154202549259389343e+03,  /* 0x40afa715845d8894 */
        1.10132329201033226127e+04,  /* 0x40c5829dd053712d */
        2.99370708659497577173e+04,  /* 0x40dd3c4489115627 */
        8.13773957125740562333e+04,  /* 0x40f3de1654d6b543 */
        2.21206696005590405548e+05,  /* 0x410b00b5916b6105 */
        6.01302142082804115489e+05,  /* 0x412259ac48bf13ca */
        1.63450868623620807193e+06,  /* 0x4138f0ccafad2d17 */
        4.44305526025399193168e+06,  /* 0x4150f2ebd0a8005c */
        1.20774763767876680940e+07,  /* 0x416709348c0ea503 */
        3.28299845686652623117e+07,  /* 0x417f4f22091940bf */
        8.92411504815936237574e+07,  /* 0x419546d8f9ed26e1 */
        2.42582597704895138741e+08,  /* 0x41aceb088b68e804 */
        6.59407867241607308388e+08,  /* 0x41c3a6e1fd9eecfd */
        1.79245642306579566002e+09,  /* 0x41dab5adb9c435ff */
        4.87240172312445068359e+09,  /* 0x41f226af33b1fdc0 */
        1.32445610649217357635e+10,  /* 0x4208ab7fb5475fb7 */
        3.60024496686929321289e+10,  /* 0x4220c3d3920962c8 */
        9.78648047144193725586e+10,  /* 0x4236c932696a6b5c */
        2.66024120300899291992e+11,  /* 0x424ef822f7f6731c */
        7.23128532145737548828e+11,  /* 0x42650bba3796379a */
        1.96566714857202099609e+12,  /* 0x427c9aae4631c056 */
        5.34323729076223046875e+12,  /* 0x429370470aec28ec */
        1.45244248326237109375e+13,  /* 0x42aa6b765d8cdf6c */
        3.94814800913403437500e+13,  /* 0x42c1f43fcc4b662c */
        1.07321789892958031250e+14,  /* 0x42d866f34a725782 */
        2.91730871263727437500e+14,  /* 0x42f0953e2f3a1ef7 */
        7.93006726156715250000e+14,  /* 0x430689e221bc8d5a */
        2.15561577355759750000e+15}; /* 0x431ea215a1d20d76 */

    unsigned long long aux, xneg;
    unsigned int ux;
    double x, y, z, z1, z2;
    int m;

    /* Special cases */

    GET_BITS_SP32(fx, ux);
    aux = ux & ~SIGNBIT_SP32;
    if (aux < 0x38800000) /* |x| small enough that cosh(x) = 1 */
    {
        if (aux == 0) return (float)1.0; /* with no inexact */
        x = fx;
        if (LAMBDA_DP64 + x  > 1.0) return (float)1.0; /* with inexact */
    }
    else if (aux >= PINFBITPATT_SP32) /* |x| is NaN or Inf */
    {
        if (aux > PINFBITPATT_SP32) /* |x| is a NaN? */
        {
#ifdef WINDOWS
            return __alm_handle_errorf(ux|QNANBITPATT_SP32, AMD_F_NONE);
#else
            return fx+fx;
#endif
        }
        else    /* x is infinity */
        {
            PUT_BITS_SP32(PINFBITPATT_SP32, fx);
            return fx;
        }
    }

    xneg = (aux != ux);

    y = fx;
    if (xneg) y = -fx;

    if (y >= max_cosh_arg)
    {
        /* Return infinity with overflow flag. */
        PUT_BITS_SP32(PINFBITPATT_SP32, fx);
        return __alm_handle_errorf(PINFBITPATT_SP32, AMD_F_OVERFLOW);
    }
    else if (y >= small_threshold)
    {
        /* In this range y is large enough so that
           the negative exponential is negligible,
           so cosh(y) is approximated by sign(x)*exp(y)/2. The
           code below is an inlined version of that from
           exp() with two changes (it operates on
           y instead of x, and the division by 2 is
           done by reducing m by 1). */

        splitexp(y, 1.0, thirtytwo_by_log2, log2_by_32_lead,
                 log2_by_32_tail, &m, &z1, &z2);
        m -= 1;

        /* scaleDouble_1 is always safe because the argument x was
           float, rather than double */

        z = scaleDouble_1((z1+z2),m);
    }
    else
    {
        /* In this range we find the integer part y0 of y 
           and the increment dy = y - y0. We then compute
 
           z = sinh(y) = sinh(y0)cosh(dy) + cosh(y0)sinh(dy)
           z = cosh(y) = cosh(y0)cosh(dy) + sinh(y0)sinh(dy)

           where sinh(y0) and cosh(y0) are tabulated above. */

        int ind;
        double dy, dy2, sdy, cdy;

        ind = (int)y;
        dy = y - ind;

        dy2 = dy*dy;

        sdy = dy + dy*dy2*(0.166666666666666667013899e0 +
                           (0.833333333333329931873097e-2 +
                            (0.198412698413242405162014e-3 +
                             (0.275573191913636406057211e-5 +
                              (0.250521176994133472333666e-7 +
                               (0.160576793121939886190847e-9 +
                                0.7746188980094184251527126e-12*dy2)*dy2)*dy2)*dy2)*dy2)*dy2);

        cdy = 1 + dy2*(0.500000000000000005911074e0 +
                       (0.416666666666660876512776e-1 +
                        (0.138888888889814854814536e-2 +
                         (0.248015872460622433115785e-4 +
                          (0.275573350756016588011357e-6 +
                           (0.208744349831471353536305e-8 +
                            0.1163921388172173692062032e-10*dy2)*dy2)*dy2)*dy2)*dy2)*dy2);

        z = cosh_lead[ind]*cdy + sinh_lead[ind]*sdy;
    }

//  if (xneg) z = - z;
    return (float)z;
}



/* ---- aocl-libm-ose src/ref/tanhf.c ---- */




float ALM_PROTO_REF(tanhf)(float x)
{
  /*
    The definition of tanh(x) is sinh(x)/cosh(x), which is also equivalent
    to the following three formulae:
    1.  (exp(x) - exp(-x))/(exp(x) + exp(-x))
    2.  (1 - (2/(exp(2*x) + 1 )))
    3.  (exp(2*x) - 1)/(exp(2*x) + 1)
    but computationally, some formulae are better on some ranges.
  */
  static const float
    thirtytwo_by_log2 =  4.6166240692e+01F, /* 0x4238aa3b */
    log2_by_32_lead =  2.1659851074e-02F, /* 0x3cb17000 */
    log2_by_32_tail =  9.9831822808e-07F, /* 0x3585fdf4 */
    large_threshold = 10.0F; /* 0x41200000 */

  unsigned int ux, aux;
  float y, z, p, z1, z2, xneg;
  int m;

  /* Special cases */

  GET_BITS_SP32(x, ux);
  aux = ux & ~SIGNBIT_SP32;
  if (aux < 0x39000000) /* |x| small enough that tanh(x) = x */
    {
        if (aux == 0x00000000)
        {
            /* x is +/-zero. Return the same zero. */
            return x;
        }
        else
        {

#ifdef WINDOWS
            return x;
#else
            return __alm_handle_errorf(ux, AMD_F_INEXACT|AMD_F_UNDERFLOW);
#endif
        }
    }
  else if (aux > 0x7f800000) /* |x| is NaN */
    {
#ifdef WINDOWS
		return __alm_handle_errorf(ux|QNANBITPATT_SP32, AMD_F_NONE);
#else
        return x+x;
#endif
    }

  xneg = 1.0F - 2.0F * (aux != ux);

  y = xneg * x;

  if (y > large_threshold)
    {
      /* If x is large then exp(-x) is negligible and
         formula 1 reduces to plus or minus 1.0 */
      z = 1.0F;
    }
  else if (y <= 1.0F)
    {
      float y2;
      y2 = y*y;

      if (y < 0.9F)
        {
          /* Use a [2,1] Remez approximation on [0,0.9]. */
          z = y + y*y2*
            (-0.28192806108402678e0F +
             (-0.14628356048797849e-2F +
              0.4891631088530669873e-4F*y2)*y2)/
            (0.845784192581041099e0F +
             0.3427017942262751343e0F*y2);
        }
      else
        {
          /* Use a [2,1] Remez approximation on [0.9,1]. */
          z = y + y*y2*
            (-0.24069858695196524e0F +
             (-0.12325644183611929e-2F +
              0.3827534993599483396e-4F*y2)*y2)/
            (0.72209738473684982e0F +
             0.292529068698052819e0F*y2);
        }
    }
  else
    {
      /* Compute p = exp(2*y) + 1. The code is basically inlined
         from exp_amd. */

      splitexpf(2*y, 1.0F, thirtytwo_by_log2, log2_by_32_lead,
	       log2_by_32_tail, &m, &z1, &z2);
      p = scaleFloat_2(z1 + z2, m) + 1.0F;
      /* Now reconstruct tanh from p. */
      z = (1.0F - 2.0F/p);
    }

    return xneg * z;
}




/* ---- aocl-libm-ose src/ref/tanf.c ---- */



/* tan(x) approximation valid on the interval [-pi/4,pi/4].
   If recip is true return -1/tan(x) instead. */
static inline double tanf_piby4(double x, int recip)
{
  double r, t;

  /* Core Remez [1,2] approximation to tan(x) on the
     interval [0,pi/4]. */
  r = x*x;
  t = x + x*r*
    (0.385296071263995406715129e0 -
     0.172032480471481694693109e-1 * r) /
    (0.115588821434688393452299e+1 +
     (-0.51396505478854532132342e0 +
      0.1844239256901656082986661e-1 * r) * r);

  if (recip)
    return -1.0 / t;
  else
    return t;
}

#ifdef WINDOWS
#endif

float ALM_PROTO_REF(tanf)(float x)
{
  double r = 0.0, dx;
  int region = 0, xneg;
  unsigned int fux;
  unsigned long long ux, ax;
  GET_BITS_SP32(x, fux);

  if ((fux & EXPBITS_SP32) == EXPBITS_SP32)
    {
      /* x is either NaN or infinity */
      if (fux & MANTBITS_SP32)
        {
          /* x is NaN */
#ifdef WINDOWS
	return  __alm_handle_errorf(fux | QNAN_MASK_32, AMD_F_NONE);
#else
	if (fux & QNAN_MASK_32)
	return  __alm_handle_errorf(fux | QNAN_MASK_32, AMD_F_NONE);
	else
	return  __alm_handle_errorf(fux | QNAN_MASK_32, AMD_F_INVALID);
#endif
        }
      else
        {
          /* x is infinity. Return a NaN */
	 return  __alm_handle_errorf(INDEFBITPATT_SP32, AMD_F_INVALID);
        }
    }

	dx = (double)x;
	GET_BITS_DP64(dx, ux);
	ax = (ux & ~SIGNBIT_DP64);
  if (ax <= 0x3fe921fb54442d18LL) /* abs(x) <= pi/4 */
    {
      if (ax < 0x3f80000000000000LL) /* abs(x) < 2.0^(-7) */
        {
          if (ax < 0x3f20000000000000LL) /* abs(x) < 2.0^(-13) */
            {
              if (ax == 0x0000000000000000LL)
                return x;
              else
#ifdef WINDOWS
                return x; //valf_with_flags(x, AMD_F_INEXACT);
#else
	return  __alm_handle_errorf(fux, AMD_F_UNDERFLOW|AMD_F_INEXACT);
#endif
            }
          else
            return (float)(dx + dx*dx*dx*0.333333333333333333);
        }
      else
        return (float)tanf_piby4(x, 0);
    }
 

  xneg = (int)(ux >> 63);

  if (xneg)
    dx = -dx;

  if (dx < 5.0e5)
    {
      /* For these size arguments we can just carefully subtract the
         appropriate multiple of pi/2, using extra precision where
         dx is close to an exact multiple of pi/2 */
      static const double
        twobypi =  6.36619772367581382433e-01, /* 0x3fe45f306dc9c883 */
        piby2_1  =  1.57079632673412561417e+00, /* 0x3ff921fb54400000 */
        piby2_1tail =  6.07710050650619224932e-11, /* 0x3dd0b4611a626331 */
        piby2_2  =  6.07710050630396597660e-11, /* 0x3dd0b4611a600000 */
        piby2_2tail =  2.02226624879595063154e-21, /* 0x3ba3198a2e037073 */
        piby2_3  =  2.02226624871116645580e-21, /* 0x3ba3198a2e000000 */
        piby2_3tail =  8.47842766036889956997e-32; /* 0x397b839a252049c1 */
      double t, rhead, rtail;
      int npi2;
      unsigned long long uy, xexp, expdiff;
      xexp  = ax >> EXPSHIFTBITS_DP64;
      /* How many pi/2 is dx a multiple of? */
      if (ax <= 0x400f6a7a2955385eLL) /* 5pi/4 */
        {
          if (ax <= 0x4002d97c7f3321d2LL) /* 3pi/4 */
            npi2 = 1;
          else
            npi2 = 2;
        }
      else if (ax <= 0x401c463abeccb2bbLL) /* 9pi/4 */
        {
          if (ax <= 0x4015fdbbe9bba775LL) /* 7pi/4 */
            npi2 = 3;
          else
            npi2 = 4;
        }
      else
        npi2  = (int)(dx * twobypi + 0.5);
      /* Subtract the multiple from dx to get an extra-precision remainder */
      rhead  = dx - npi2 * piby2_1;
      rtail  = npi2 * piby2_1tail;
      GET_BITS_DP64(rhead, uy);
      expdiff = xexp - ((uy & EXPBITS_DP64) >> EXPSHIFTBITS_DP64);
      if (expdiff > 15)
        {
          /* The remainder is pretty small compared with dx, which
             implies that dx is a near multiple of pi/2
             (dx matches the multiple to at least 15 bits) */
          t  = rhead;
          rtail  = npi2 * piby2_2;
          rhead  = t - rtail;
          rtail  = npi2 * piby2_2tail - ((t - rhead) - rtail);
          if (expdiff > 48)
            {
              /* dx matches a pi/2 multiple to at least 48 bits */
              t  = rhead;
              rtail  = npi2 * piby2_3;
              rhead  = t - rtail;
              rtail  = npi2 * piby2_3tail - ((t - rhead) - rtail);
            }
        }
      r = rhead - rtail;
      region = npi2 & 3;
    }
  else
    {
		/* Reduce x into range [-pi/4,pi/4] */
		__amd_remainder_piby2d2f(ax, &r, &region);
    }

  if (xneg)
    return (float)-tanf_piby4(r, region & 1);
  else
    return (float)tanf_piby4(r, region & 1);
}


