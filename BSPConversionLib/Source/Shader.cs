using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BSPConversionLib
{
	public enum AlphaFunc
	{
		GLS_ATEST_GT_0 = 0x10000000,
		GLS_ATEST_LT_80 = 0x20000000,
		GLS_ATEST_GE_80 = 0x40000000
	}

	public enum CullType
	{
		FRONT_SIDED,
		BACK_SIDED,
		TWO_SIDED
	}

	public class Shader
	{
		public class SkyParms
		{
			public string outerBox;
			public string cloudHeight;
			public string innerBox;
		}

		public string map; // Path to image file
		public SkyParms skyParms;
		public Q3SurfaceFlags surfaceFlags;
		public Q3ContentsFlags contents;
		public CullType cullType;

		// Shader stage parameters (TODO: Needs to be moved to separate class for handling stages)
		public AlphaFunc alphaFunc;
	}
}
