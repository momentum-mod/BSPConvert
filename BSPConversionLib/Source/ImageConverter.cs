using DevILSharp;
using System.Runtime.InteropServices;
using VTFLib;

namespace BSPConversionLib
{
	public class ImageConverter
	{
		public void Convert(string imagePath)
		{
			if (!IL.LoadImage(imagePath))
			{
				Console.WriteLine($"Failed to convert image to vtf: {imagePath}");
				return;
			}

			if (!IL.ConvertImage(ChannelFormat.RGBA, ChannelType.UnsignedByte))
			{
				Console.WriteLine($"Failed to convert image to vtf: {imagePath}");
				return;
			}

			var width = IL.GetInteger(IntName.ImageWidth);
			var height = IL.GetInteger(IntName.ImageHeight);

			var size = width * height * 4;
			var data = new byte[size];
			Marshal.Copy(IL.GetData(), data, 0, size);

			var createOptions = new SVTFCreateOptions();
			VTFFile.ImageCreateDefaultCreateStructure(ref createOptions);
			createOptions.imageFormat = IL.GetInteger(IntName.ImageBytesPerPixel) == 4 ?
				VTFImageFormat.IMAGE_FORMAT_DXT5 : VTFImageFormat.IMAGE_FORMAT_DXT1;
			createOptions.resize = true;

			if (!VTFFile.ImageCreateSingle((uint)width, (uint)height, data, ref createOptions))
			{
				Console.WriteLine($"Failed to convert image to vtf: {imagePath}");
				return;
			}

			var savePath = Path.ChangeExtension(imagePath, ".vtf");
			if (!VTFFile.ImageSave(savePath))
				Console.WriteLine($"Failed to convert image to vtf: {imagePath}");
		}
	}
}
