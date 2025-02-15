namespace BSPConvert.Test;

using BSPConvert.Lib;

public class Tests
{
    [SetUp]
    public void Setup()
    {
        // Clear "Converted" folder
        string outputDir = Path.Combine(TestContext.CurrentContext.TestDirectory, "Converted");
        if (Directory.Exists(outputDir))
            Directory.Delete(outputDir, true);

        Directory.CreateDirectory(outputDir);
    }

    [Test]
    public void ConvertTestFiles()
    {
        string testFilesDir = Path.Combine(TestContext.CurrentContext.TestDirectory, "Test Files");
        string[] files = Directory.GetFiles(testFilesDir, "*.bsp", SearchOption.AllDirectories);
        foreach (string file in files)
            Convert(file);

        Assert.Pass();
    }

    private void Convert(string bspFile)
    {
        string outputDir = Path.Combine(TestContext.CurrentContext.TestDirectory, "Converted");
        var options = new BSPConverterOptions()
        {
            NoPak = false,
            DisplacementPower = 4,
            MinDamageToConvertTrigger = 50,
            IgnoreZones = false,
            OldBsp = false,
            Prefix = "df_",
            InputFile = bspFile,
            OutputDir = outputDir,
        };

        var converter = new BSPConverter(options, new DebugLogger());
        converter.Convert();
    }
}
