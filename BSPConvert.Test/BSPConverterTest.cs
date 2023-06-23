using BSPConvert.Lib;

namespace BSPConvert.Test
{
	public class Tests
	{
		[SetUp]
		public void Setup()
		{
			// Clear "Converted" folder
			var outputDir = Path.Combine(TestContext.CurrentContext.TestDirectory, "Converted");
			if (Directory.Exists(outputDir))
				Directory.Delete(outputDir, true);

			Directory.CreateDirectory(outputDir);
		}

		[Test]
		public void ConvertTestFiles()
		{
			var testFilesDir = Path.Combine(TestContext.CurrentContext.TestDirectory, "Test Files");
			var files = Directory.GetFiles(testFilesDir, "*.bsp", SearchOption.AllDirectories);
			foreach (var file in files)
				Convert(file);
			
			Assert.Pass();
		}

		private void Convert(string bspFile)
		{
			var outputDir = Path.Combine(TestContext.CurrentContext.TestDirectory, "Converted");
			var options = new BSPConverterOptions()
			{
				noPak = false,
				DisplacementPower = 4,
				minDamageToConvertTrigger = 50,
				oldBSP = false,
				prefix = "df_",
				inputFile = bspFile,
				outputDir = outputDir
			};
			
			var converter = new BSPConverter(options, new DebugLogger());
			converter.Convert();
		}
	}
}