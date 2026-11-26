using System.Reflection;
using ChipSharp.Backend.Llvm;
using ChipSharp.Frontend;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Ir;
using ChipSharp.Runtime.Rp2040;
using ChipSharp.Runtime.Windows;
using ChipSharp.Targets;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Library;

/// <summary>
/// Library substitutes (<see cref="LibrarySubstitutes"/>): ChipSharp's own C# for library types, in ChipSharp.Library,
/// compiled with the programs that use them. What links to them, what the whole-program pass reaches and checks, what
/// stays a library member ChipSharp does not have, and how they reach the runtime.
/// </summary>
public sealed class LibrarySubstituteTests
{
    private static readonly TargetLoader Targets = new(Path.Combine(AppContext.BaseDirectory, "targets"));

    [Fact]
    public void The_library_next_to_the_frontend_has_substitutes_for_TimeSpan_and_Timing()
    {
        Assert.Equal(
            ["ChipSharp.Library.TimeSpan for System.TimeSpan", "ChipSharp.Library.Timing for ChipSharp.Hardware.Timing"],
            LibrarySubstitutes.Substituted.Select(pair => $"{pair.Key.FullName} for {pair.Value}").Order(StringComparer.Ordinal));
        Assert.Equal(
            Path.Combine(Path.GetDirectoryName(typeof(LibrarySubstitutes).Assembly.Location)!, LibrarySubstitutes.FileName),
            LibrarySubstitutes.Assembly.FilePath);
        Assert.Null(LibrarySubstitutes.Assembly.EntryPoint);
    }

    [Fact]
    public void The_factories_in_any_unit_bind_to_the_overloads_dotnet_10_has_and_each_links_to_its_substitute()
    {
        var main = Load("""
            using System;

            class Program
            {
                static void Main()
                {
                    Console.WriteLine(TimeSpan.FromMilliseconds(500).Ticks);
                    Console.WriteLine(TimeSpan.FromSeconds(2).Ticks);
                    Console.WriteLine(TimeSpan.FromMicroseconds(250).Ticks);
                    Console.WriteLine(TimeSpan.FromDays(1).Ticks);
                    Console.WriteLine(TimeSpan.FromSeconds(1.5).Ticks);
                }
            }
            """).EntryPoint!;

        var calls = Calls(main);

        // an int converts better to long than to double: FromMilliseconds(500) is FromMilliseconds(long), and FromDays has
        // an int overload of its own; FromSeconds(long, long = 0, long = 0) loses to FromSeconds(long)
        Assert.Equal(
            ["FromMilliseconds(int64)", "get_Ticks()", "WriteLine(int64)", "FromSeconds(int64)", "get_Ticks()",
                "WriteLine(int64)", "FromMicroseconds(int64)", "get_Ticks()", "WriteLine(int64)", "FromDays(int32)",
                "get_Ticks()", "WriteLine(int64)", "FromSeconds(float64)", "get_Ticks()", "WriteLine(int64)"],
            calls.Select(call => $"{call.Name}({string.Join(", ", call.ParameterTypes)})"));
        Assert.All(calls.Where(call => call.Name != "WriteLine"), call =>
        {
            Assert.Equal("System.TimeSpan", ((IlNamedType)call.DeclaringType).FullName);
            Assert.False(((IlNamedType)call.DeclaringType).IsDefinedInThisAssembly);
            var substitute = Assert.IsType<IlMethod>(call.Definition);
            Assert.Same(LibrarySubstitutes.Assembly, substitute.Assembly);
            Assert.Equal($"ChipSharp.Library.TimeSpan.{call.Name}", substitute.DisplayName);
        });
        Assert.All(calls.Where(call => call.Name == "WriteLine"), call => Assert.Null(call.Definition));
    }

    [Fact]
    public void The_whole_program_pass_reaches_the_substitute_members_the_program_uses_and_nothing_else()
    {
        var program = WholeProgramPass.Analyze(Load("""
            using System;

            class Program
            {
                static void Main() => Console.WriteLine(TimeSpan.FromSeconds(2).Ticks);
            }
            """));

        Assert.Equal(
            ["Program.Main()", "ChipSharp.Library.TimeSpan.FromSeconds(long)", "ChipSharp.Library.TimeSpan.get_Ticks()",
                "ChipSharp.Library.TimeSpan.FromUnits(long, long, long, long)",
                "ChipSharp.Library.TimeSpan.FromTicks(long)", "ChipSharp.Library.TimeSpan..ctor(long)"],
            program.Methods.Select(CSharpNames.Method));

        // TimeSpan.Zero and the other fields are not used, so their initializer is not part of the program
        Assert.Empty(program.TypeInitializers);
    }

    [Fact]
    public void A_field_of_a_substitute_links_to_its_field_and_its_initializer_runs_before_Main()
    {
        var program = WholeProgramPass.Analyze(Load("""
            using System;

            class Program
            {
                static void Main()
                {
                    Console.WriteLine(TimeSpan.MaxValue.Ticks);
                    Console.WriteLine(TimeSpan.Zero == TimeSpan.FromTicks(0));
                }
            }
            """));

        var fields = program.EntryPoint.Body!.Instructions.Select(i => i.Operand).OfType<IlFieldOperand>()
            .Select(operand => operand.Field).ToList();
        Assert.Equal(["MaxValue", "Zero"], fields.Select(field => field.Name));
        Assert.All(fields, field => Assert.Equal("ChipSharp.Library.TimeSpan", field.Definition?.DeclaringType.FullName));
        var initializer = Assert.Single(program.TypeInitializers);
        Assert.Equal("ChipSharp.Library.TimeSpan..cctor", initializer.DisplayName);

        var module = IlToTir.Lower(program);
        Assert.Equal(ProgramFunctions.StartName, module.EntryPoint!.Name);
        Assert.Contains(module.Globals, global => global.Name == "ChipSharp.Library.TimeSpan.MaxValue");
    }

    [Fact]
    public void A_library_member_without_a_substitute_is_still_a_library_method_that_is_not_available()
    {
        var analysis = WholeProgramPass.Check(Load("""
            using System;

            class Program
            {
                static void Main()
                {
                    Console.WriteLine(TimeSpan.FromMilliseconds(1, 500).Ticks);
                    Console.WriteLine(TimeSpan.FromSeconds(2).Ticks);
                }
            }
            """));

        var problem = Assert.Single(analysis.Problems);
        Assert.True(problem.IsLibraryCall);
        Assert.Equal("the library method System.TimeSpan.FromMilliseconds(long, long)", problem.Feature);
        Assert.Equal("Program.Main", problem.Method.DisplayName);
    }

    [Fact]
    public void Substitutes_call_substitutes_Timing_Delay_reads_TimeSpan_Ticks_through_the_library_type()
    {
        var program = WholeProgramPass.Analyze(LoadWithHardware("""
            using System;
            using ChipSharp.Hardware;

            Timing.Delay(TimeSpan.FromMilliseconds(500));
            Timing.DelayMilliseconds(1);
            """));

        var delay = Assert.Single(program.Methods, method => method.DisplayName == "ChipSharp.Library.Timing.Delay");
        Assert.Same(delay, Assert.Single(Calls(program.EntryPoint), call => call.Name == "Delay").Definition);

        // Delay's IL names System.TimeSpan's get_Ticks, as the program's would: it links to the TimeSpan substitute
        var ticks = Assert.Single(Calls(delay), call => call.Name == "get_Ticks");
        Assert.Equal("[System.Runtime]System.TimeSpan", ticks.DeclaringType.ToString());
        Assert.Equal("ChipSharp.Library.TimeSpan.get_Ticks", ticks.Definition?.DisplayName);
        Assert.Contains(ticks.Definition, program.Methods);

        // and its own calls of ChipSharp.Hardware stay the runtime's functions
        var module = IlToTir.Lower(program);
        Assert.Equal(
            ["ChipSharp.Runtime.DelayMicroseconds", "ChipSharp.Runtime.DelayMilliseconds", "ChipSharp.Runtime.TimeSpanOutOfRange"],
            module.Functions.Where(function => function.Kind == TirFunctionKind.External).Select(function => function.Name)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_TimeSpan_anywhere_in_the_program_is_the_one_struct_type_of_the_substitute_with_the_ticks_long()
    {
        var program = WholeProgramPass.Analyze(Load("""
            using System;

            class Program
            {
                static TimeSpan last;

                static void Main()
                {
                    var blink = new Blink { Period = TimeSpan.FromMilliseconds(250) };
                    TimeSpan[] laps = { blink.Period, Twice(blink.Period) };
                    last = laps[1];
                    Console.WriteLine(last.Ticks);
                }

                static TimeSpan Twice(TimeSpan span) => span + span;
            }

            struct Blink
            {
                public int Pin;
                public TimeSpan Period;
            }
            """));

        var module = IlToTir.Lower(program);

        var timeSpan = Assert.Single(module.FindStructTypes(), type => type.Name == "ChipSharp.Library.TimeSpan");
        Assert.Equal([("_ticks", TirType.I64)], timeSpan.Fields.Select(field => (field.Name, field.Type)));
        var blink = Assert.Single(module.FindStructTypes(), type => type.Name == "Blink");
        Assert.Same(timeSpan, blink.Fields[1].Type);
        Assert.Same(timeSpan, Assert.Single(module.Globals, global => global.Name == "Program.last").Type);
        var twice = Assert.Single(module.Functions, function => function.Name == "Program.Twice");
        Assert.Same(timeSpan, twice.ReturnType);
        Assert.Same(timeSpan, twice.Parameters[0].Type);
        Assert.Same(timeSpan, Assert.Single(module.Functions, function => function.Name == "ChipSharp.Library.TimeSpan.op_Addition")
            .ReturnType);
    }

    [Fact]
    public void A_runtime_function_of_the_library_is_a_call_to_the_runtime_and_its_dotnet_body_is_left_out()
    {
        var program = WholeProgramPass.Analyze(Load("""
            using System;

            class Program
            {
                static void Main() => Console.WriteLine((TimeSpan.MaxValue + TimeSpan.FromTicks(1)).Ticks);
            }
            """));

        Assert.DoesNotContain(program.Methods, method => method.DeclaringType.FullName == LibrarySubstitutes.RuntimeType);
        var addition = Assert.Single(program.Methods, method => method.Name == "op_Addition");
        var overflow = Assert.Single(Calls(addition), call => call.Name == "TimeSpanOverflow");
        Assert.Null(overflow.Definition);
        Assert.True(KnownExternals.TryMap(overflow, out var function));
        Assert.Equal(("ChipSharp.Runtime.TimeSpanOverflow", TirType.Void), (function.DisplayName, function.ReturnType));
        Assert.Empty(function.ParameterTypes);

        var module = IlToTir.Lower(program);
        var external = Assert.Single(module.Functions, f => f.Name == "ChipSharp.Runtime.TimeSpanOverflow");
        Assert.Equal(TirFunctionKind.External, external.Kind);
    }

    [Fact]
    public void Every_runtime_function_of_the_library_is_in_every_runtime()
    {
        var functions = LibrarySubstitutes.Assembly.FindType(LibrarySubstitutes.RuntimeType)!.Methods
            .Where(method => method.IsStatic && method.Name != ".cctor").ToList();
        var windows = new WindowsX64Runtime().Startup(Targets.Load("host")).Files
            .Single(file => file.FileName == WindowsX64Runtime.FailuresFileName).Content;
        var pico = new Rp2040Runtime().Startup(Targets.Load("pico")).Files
            .Single(file => file.FileName == Rp2040Runtime.FailuresFileName).Content;

        Assert.Equal(7, functions.Count);
        Assert.All(functions, method =>
        {
            var symbol = LlvmCodeGenerator.SymbolName($"ChipSharp.Runtime.{method.Name}");
            Assert.Contains($"__declspec(noreturn) void {symbol}(void)", windows);
            Assert.Contains($"__attribute__((noreturn)) void {symbol}(void)", pico);
        });
    }

    [Fact]
    public void On_dotnet_each_runtime_function_throws_the_exception_dotnet_throws_there()
    {
        var thrown = typeof(global::ChipSharp.Library.Runtime).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .OrderBy(method => method.Name)
            .Select(method => Assert.Throws<TargetInvocationException>(() => method.Invoke(null, null)).InnerException!)
            .Select(exception => $"{exception.GetType().Name}: {exception.Message}");

        Assert.Equal(
            [
                "OverflowException: Negating the minimum value of a twos complement number is invalid.",
                "OverflowException: The duration cannot be returned for TimeSpan.MinValue because the absolute value of " +
                    "TimeSpan.MinValue exceeds the value of TimeSpan.MaxValue.",
                "ArgumentException: TimeSpan does not accept floating point Not-a-Number values.",
                "ArgumentException: TimeSpan does not accept floating point Not-a-Number values. (Parameter 'divisor')",
                "ArgumentException: TimeSpan does not accept floating point Not-a-Number values. (Parameter 'factor')",
                "ArgumentOutOfRangeException: TimeSpan overflowed because the duration is too long.",
                "OverflowException: TimeSpan overflowed because the duration is too long.",
            ],
            thrown);
    }

    [Fact]
    public void An_interrupt_handler_cannot_wait_with_Timing_Delay_either_and_the_problem_is_where_it_calls_it()
    {
        var analysis = WholeProgramPass.Check(LoadWithHardware("""
            using System;
            using ChipSharp.Hardware;

            Timing.EveryMilliseconds(10);
            Timing.Delay(TimeSpan.FromSeconds(1));

            partial class Program
            {
                [Interrupt(Event.Timer)]
                static void Tick() => Timing.Delay(TimeSpan.FromMicroseconds(5));
            }
            """));

        var problem = Assert.Single(analysis.Problems);
        Assert.Same(Unsupported.WaitInHandler, problem.Unsupported);
        Assert.Equal("Program.Tick", problem.Method.DisplayName);
        Assert.Equal("Delay", ((IlMethodOperand)problem.Method.Body!.Instructions.Single(i => i.Offset == problem.Offset)
            .Operand!).Method.Name);
    }

    [Fact]
    public void A_readonly_field_that_Main_and_a_handler_read_through_its_address_is_not_shared_writing()
    {
        // TimeSpan.Zero.Ticks takes Zero's address (ldsflda): a readonly struct needs no copy
        var analysis = WholeProgramPass.Check(LoadWithHardware("""
            using System;
            using ChipSharp.Hardware;

            Timing.EveryMilliseconds(10);
            Console.WriteLine(TimeSpan.Zero.Ticks);

            partial class Program
            {
                [Interrupt(Event.Timer)]
                static void Tick() => Pin.Write(25, TimeSpan.Zero.Ticks == 0);
            }
            """));

        Assert.True(analysis.IsSupported, analysis.Details);
        Assert.Contains(analysis.Methods.Single(method => method.DisplayName == "Program.Tick").Body!.Instructions,
            instruction => instruction.OpCode == IlOpCode.Ldsflda);
    }

    [Fact]
    public void The_substitutes_stand_for_these_library_members()
    {
        Assert.Equal(
            ["System.TimeSpan.Zero", "System.TimeSpan.new TimeSpan(long)", "System.TimeSpan.Ticks",
                "System.TimeSpan.FromMilliseconds(long)", "System.TimeSpan.FromMilliseconds(double)",
                "System.TimeSpan.operator +(System.TimeSpan, System.TimeSpan)",
                "ChipSharp.Hardware.Timing.Delay(System.TimeSpan)"],
            LibrarySubstitutes.Members.Where(member => member is "System.TimeSpan.Zero" or "System.TimeSpan.new TimeSpan(long)"
                or "System.TimeSpan.Ticks" or "System.TimeSpan.operator +(System.TimeSpan, System.TimeSpan)"
                || member.Contains(".FromMilliseconds(", StringComparison.Ordinal)
                || member.Contains("Timing", StringComparison.Ordinal)));
        Assert.DoesNotContain(LibrarySubstitutes.Members, member => member.Contains("Interval", StringComparison.Ordinal));
        Assert.Equal(LibrarySubstitutes.Members.Count, LibrarySubstitutes.Members.Distinct().Count());
    }

    private static IlAssembly Load(string source) => IlAssembly.Load(CSharpCompiler.Compile(source, "Test"), "Test.dll");

    private static IlAssembly LoadWithHardware(string source) =>
        IlAssembly.Load(CSharpCompiler.Compile([source], [PicoBuild.HardwareAssembly], "Test"), "Test.dll");

    /// <summary>The methods <paramref name="method"/> calls (and the constructors newobj calls), in order.</summary>
    private static List<IlMethodReference> Calls(IlMethod method) =>
        [.. method.Body!.Instructions.Where(i => i.OpCode is IlOpCode.Call or IlOpCode.Newobj)
            .Select(i => ((IlMethodOperand)i.Operand!).Method)];
}
