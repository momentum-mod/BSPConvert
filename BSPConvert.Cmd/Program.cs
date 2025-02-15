namespace BSPConvert.Cmd;

using BSPConvert.Lib;
using CommandLine;
using CommandLine.Text;

internal sealed class Program
{
    private sealed class Options
    {
        [Option(
            "nopak",
            Required = false,
            HelpText = "Export materials into folders instead of embedding them in the BSP."
        )]
        public bool NoPak { get; set; }

        [Option(
            "notooldisps",
            Required = false,
            HelpText = "Skip converting patches with tool textures to displacements."
        )]
        public bool NoToolDisplacements { get; set; }

        [Option("subdiv", Required = false, Default = 4, HelpText = "Displacement subdivisions [2-4].")]
        public int DisplacementPower { get; set; }

        [Option(
            "mindmg",
            Required = false,
            Default = 50,
            HelpText = "Minimum damage to convert trigger_hurt into trigger_teleport."
        )]
        public int MinDamageToConvertTrigger { get; set; }

        [Option("nozones", Required = false, HelpText = "Ignore timer zone triggers.")]
        public bool IgnoreZones { get; set; }

        //[Option("oldbsp", Required = false, HelpText = "Use BSP version 20 (HL2 / CS:S).")]
        //public bool OldBSP { get; set; }

        [Option("prefix", Required = false, Default = "df_", HelpText = "Prefix for the converted BSP's file name.")]
        public string Prefix { get; set; }

        [Option("output", Required = false, HelpText = "Output game directory for converted BSP/materials.")]
        public string OutputDirectory { get; set; }

        [Value(
            0,
            MetaName = "input files",
            Required = true,
            HelpText = "Input Quake 3 BSP/PK3 file(s) to be converted."
        )]
        public IEnumerable<string> InputFiles { get; set; }
    }

    private static void Main(string[] args)
    {
        //args = new string[]
        //{
        //	@"c:\users\tyler\documents\tools\source engine\bspconvert\dfwc2017-6.pk3",
        //	"--output", @"c:\users\tyler\documents\tools\source engine\bspconvert\output",
        //};

        var parser = new Parser(with => with.HelpWriter = null);
        ParserResult<Options> parserResult = parser.ParseArguments<Options>(args);
        parserResult.WithParsed(RunCommand).WithNotParsed(errors => DisplayHelp(errors, parserResult));
    }

    private static void RunCommand(Options options)
    {
        if (options.DisplacementPower is < 2 or > 4)
            throw new ArgumentOutOfRangeException("Displacement power must be between 2 and 4.");

        options.OutputDirectory ??= Path.GetDirectoryName(options.InputFiles.First());

        foreach (string inputEntry in options.InputFiles)
        {
            var converterOptions = new BSPConverterOptions()
            {
                noPak = options.NoPak,
                noToolDisplacements = options.NoToolDisplacements,
                DisplacementPower = options.DisplacementPower,
                minDamageToConvertTrigger = options.MinDamageToConvertTrigger,
                ignoreZones = options.IgnoreZones,
                //oldBSP = options.OldBSP,
                prefix = options.Prefix,
                inputFile = inputEntry,
                outputDir = options.OutputDirectory,
            };
            var converter = new BSPConverter(converterOptions, new ConsoleLogger());
            converter.Convert();
        }
    }

    private static void DisplayHelp(IEnumerable<Error> errors, ParserResult<Options> parserResult)
    {
        const string version = "BSP Convert 0.0.3-alpha";
        if (errors.IsVersion())
        {
            Console.WriteLine(version);
            return;
        }

        var helpText = HelpText.AutoBuild(
            parserResult,
            h =>
            {
                h.AdditionalNewLineAfterOption = false;
                h.MaximumDisplayWidth = 400;
                h.Heading = version;
                h.Copyright = "";
                h.AddPostOptionsLine(
                    "EXAMPLE:\n  .\\BSPConv.exe \"C:\\Users\\<username>\\Documents\\BSPConvert\\nood-aDr.pk3\" --output \"C:\\Program Files (x86)\\Steam\\steamapps\\common\\Momentum Mod Playtest\\momentum\" --prefix \"df_\""
                );

                return HelpText.DefaultParsingErrorsHandler(parserResult, h);
            },
            e => e
        );
        Console.WriteLine(helpText);
    }
}
