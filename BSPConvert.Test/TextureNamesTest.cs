using BSPConvert.Lib;

namespace BSPConvert.Test
{
	public class TextureNamesTests
	{
		private string dir;

		[SetUp]
		public void Setup()
		{
			dir = Path.Combine(Path.GetTempPath(), "BSPConvert.TextureNamesTests." + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Path.Combine(dir, "textures", "cos1"));
		}

		[TearDown]
		public void TearDown()
		{
			Directory.Delete(dir, true);
		}

		[Test]
		public void RenamesDottedTexturesAndTheirReferences()
		{
			Write("textures/cos1/cretelighttrim01.blend.vtf", "glow");
			Write("textures/cos1/cretetrimlight2.vtf", "base");
			// Already taken, so the renamed glow gets a number
			Write("textures/cos1/steplight1_blend.vtf", "other");
			Write("textures/cos1/steplight1.blend.vtf", "glow 2");
			Write("textures/cos1/cretetrimlight2.vmt", """
				LightmappedGeneric
				{
					$basetexture "textures/cos1/cretetrimlight2"
					$detail "textures/cos1/CreteLightTrim01.blend"
					$envmap textures/cos1/steplight1.blend.vtf
				}
				""");

			Assert.That(TextureNames.FixDottedTextureNames(dir), Is.EqualTo(2));

			Assert.That(File.ReadAllText(Path.Combine(dir, "textures/cos1/cretelighttrim01_blend.vtf")), Is.EqualTo("glow"));
			Assert.That(File.ReadAllText(Path.Combine(dir, "textures/cos1/steplight1_blend_1.vtf")), Is.EqualTo("glow 2"));
			Assert.That(File.ReadAllText(Path.Combine(dir, "textures/cos1/steplight1_blend.vtf")), Is.EqualTo("other"));
			Assert.That(File.Exists(Path.Combine(dir, "textures/cos1/cretelighttrim01.blend.vtf")), Is.False);

			var vmt = File.ReadAllText(Path.Combine(dir, "textures/cos1/cretetrimlight2.vmt"));
			Assert.That(vmt, Does.Contain("$basetexture \"textures/cos1/cretetrimlight2\""));
			Assert.That(vmt, Does.Contain("$detail \"textures/cos1/cretelighttrim01_blend\""));
			Assert.That(vmt, Does.Contain("$envmap textures/cos1/steplight1_blend_1"));
		}

		[Test]
		public void LeavesTexturesWithoutDotsAlone()
		{
			Write("textures/cos1/cretetrimlight2.vtf", "base");
			Write("textures/cos1/cretetrimlight2.vmt", "LightmappedGeneric { $basetexture \"textures/cos1/cretetrimlight2\" }");

			Assert.That(TextureNames.FixDottedTextureNames(dir), Is.EqualTo(0));
			Assert.That(File.Exists(Path.Combine(dir, "textures/cos1/cretetrimlight2.vtf")));
		}

		private void Write(string path, string text) => File.WriteAllText(Path.Combine(dir, path), text);
	}
}
