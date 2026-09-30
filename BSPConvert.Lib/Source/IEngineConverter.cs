using LibBSP;

namespace BSPConvert.Lib
{
	// Converts a map from one input engine (e.g. Quake 3) into the Source BSP held by a SourceBspBuilder.
	// BSPConverter picks the implementation from the input BSP's MapType and handles writing the output.
	public interface IEngineConverter
	{
		void Convert(BSP inputBsp, SourceBspBuilder output);
	}
}
