using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BSPConvert.Lib
{
	[Flags]
	public enum Q3ContentsFlags : uint
	{
		CONTENTS_SOLID = 1,     // an eye is never valid in a solid
		CONTENTS_LAVA = 8,
		CONTENTS_SLIME = 16,
		CONTENTS_WATER = 32,
		CONTENTS_FOG = 64,

		CONTENTS_NOTTEAM1 = 0x0080,
		CONTENTS_NOTTEAM2 = 0x0100,
		CONTENTS_NOBOTCLIP = 0x0200,

		CONTENTS_AREAPORTAL = 0x8000,

		CONTENTS_PLAYERCLIP = 0x10000,
		CONTENTS_MONSTERCLIP = 0x20000,
		//bot specific contents types
		CONTENTS_TELEPORTER = 0x40000,
		CONTENTS_JUMPPAD = 0x80000,
		CONTENTS_CLUSTERPORTAL = 0x100000,
		CONTENTS_DONOTENTER = 0x200000,
		CONTENTS_BOTCLIP = 0x400000,
		CONTENTS_MOVER = 0x800000,

		CONTENTS_ORIGIN = 0x1000000,    // removed before bsping an entity

		CONTENTS_BODY = 0x2000000,  // should never be on a brush, only in game
		CONTENTS_CORPSE = 0x4000000,
		CONTENTS_DETAIL = 0x8000000,    // brushes not used for the bsp
		CONTENTS_STRUCTURAL = 0x10000000,   // brushes used for the bsp
		CONTENTS_TRANSLUCENT = 0x20000000,  // don't consume surface fragments inside
		CONTENTS_TRIGGER = 0x40000000,
		CONTENTS_NODROP = 0x80000000    // don't leave bodies or items (death fog, lava)
	}

	[Flags]
	public enum SourceContentsFlags
	{
		CONTENTS_EMPTY			=	0,		// No contents
		
		CONTENTS_SOLID			=	0x1,		// an eye is never valid in a solid
		CONTENTS_WINDOW			=	0x2,		// translucent, but not watery (glass)
		CONTENTS_AUX			=	0x4,
		CONTENTS_GRATE			=	0x8,		// alpha-tested "grate" textures.  Bullets/sight pass through, but solids don't
		CONTENTS_SLIME			=	0x10,
		CONTENTS_WATER			=	0x20,
		CONTENTS_BLOCKLOS		=	0x40,	// block AI line of sight
		CONTENTS_OPAQUE			=	0x80,	// things that cannot be seen through (may be non-solid though)
		LAST_VISIBLE_CONTENTS	=	0x80,
		
		ALL_VISIBLE_CONTENTS	=	(LAST_VISIBLE_CONTENTS | (LAST_VISIBLE_CONTENTS-1)),
		
		CONTENTS_TESTFOGVOLUME	=	0x100,
		CONTENTS_UNUSED			=	0x200,	
		
		// unused 
		// NOTE: If it's visible, grab from the top + update LAST_VISIBLE_CONTENTS
		// if not visible, then grab from the bottom.
		CONTENTS_UNUSED6		=	0x400,
		
		CONTENTS_TEAM1			=	0x800,	// per team contents used to differentiate collisions 
		CONTENTS_TEAM2			=	0x1000,	// between players and objects on different teams
		
		// ignore CONTENTS_OPAQUE on surfaces that have SURF_NODRAW
		CONTENTS_IGNORE_NODRAW_OPAQUE = 0x2000,
		
		// hits entities which are MOVETYPE_PUSH (doors, plats, etc.)
		CONTENTS_MOVEABLE		=	0x4000,
		
		// remaining contents are non-visible, and don't eat brushes
		CONTENTS_AREAPORTAL		=	0x8000,
		
		CONTENTS_PLAYERCLIP		=	0x10000,
		CONTENTS_MONSTERCLIP	=	0x20000,
		
		// currents can be added to any other contents, and may be mixed
		CONTENTS_CURRENT_0		=	0x40000,
		CONTENTS_CURRENT_90		=	0x80000,
		CONTENTS_CURRENT_180	=	0x100000,
		CONTENTS_CURRENT_270	=	0x200000,
		CONTENTS_CURRENT_UP		=	0x400000,
		CONTENTS_CURRENT_DOWN	=	0x800000,
		
		CONTENTS_ORIGIN			=	0x1000000,	// removed before bsping an entity
		
		CONTENTS_MONSTER		=	0x2000000,	// should never be on a brush, only in game
		CONTENTS_DEBRIS			=	0x4000000,
		CONTENTS_DETAIL			=	0x8000000,	// brushes to be added after vis leafs
		CONTENTS_TRANSLUCENT	=	0x10000000,	// auto set if any surface has trans
		CONTENTS_LADDER			=	0x20000000,
		CONTENTS_HITBOX			=	0x40000000	// use accurate hitboxes on trace
	}

	[Flags]
	public enum Q3SurfaceFlags
	{
		SURF_NODAMAGE = 0x1,        // never give falling damage
		SURF_SLICK = 0x2,       // effects game physics
		SURF_SKY = 0x4,     // lighting from environment map
		SURF_LADDER = 0x8,
		SURF_NOIMPACT = 0x10,       // don't make missile explosions
		SURF_NOMARKS = 0x20,        // don't leave missile marks
		SURF_FLESH = 0x40,      // make flesh sounds and effects
		SURF_NODRAW = 0x80,     // don't generate a drawsurface at all
		SURF_HINT = 0x100,      // make a primary bsp splitter
		SURF_SKIP = 0x200,      // completely ignore, allowing non-closed brushes
		SURF_NOLIGHTMAP = 0x400,        // surface doesn't need a lightmap
		SURF_POINTLIGHT = 0x800,        // generate lighting info at vertexes
		SURF_METALSTEPS = 0x1000,       // clanking footsteps
		SURF_NOSTEPS = 0x2000,      // no footstep sounds
		SURF_NONSOLID = 0x4000,     // don't collide against curves with this set
		SURF_LIGHTFILTER = 0x8000,      // act as a light filter during q3map -light
		SURF_ALPHASHADOW = 0x10000, // do per-pixel light shadow casting in q3map
		SURF_NODLIGHT = 0x20000,    // don't dlight even if solid (solid lava, skies)
		SURF_DUST = 0x40000     // leave a dust trail when walking on this surface
	}

	[Flags]
	public enum SourceSurfaceFlags
	{
		SURF_LIGHT = 0x0001,		// value will hold the light strength
		SURF_SKY2D = 0x0002,		// don't draw, indicates we should skylight + draw 2d sky but not draw the 3D skybox
		SURF_SKY = 0x0004,		// don't draw, but add to skybox
		SURF_WARP = 0x0008,		// turbulent water warp
		SURF_TRANS = 0x0010,
		SURF_NOPORTAL = 0x0020,	// the surface can not have a portal placed on it
		SURF_TRIGGER = 0x0040,	// FIXME: This is an xbox hack to work around elimination of trigger surfaces, which breaks occluders
		SURF_NODRAW = 0x0080,	// don't bother referencing the texture
		
		SURF_HINT = 0x0100,	// make a primary bsp splitter
		
		SURF_SKIP = 0x0200,	// completely ignore, allowing non-closed brushes
		SURF_NOLIGHT = 0x0400,	// Don't calculate light
		SURF_BUMPLIGHT = 0x0800,	// calculate three lightmaps for the surface for bumpmapping
		SURF_NOSHADOWS = 0x1000,	// Don't receive shadows
		SURF_NODECALS = 0x2000,	// Don't receive decals
		SURF_NOPAINT = SURF_NODECALS,	// the surface can not have paint placed on it
		SURF_NOCHOP = 0x4000,	// Don't subdivide patches on this surface 
		SURF_HITBOX = 0x8000,	// surface is part of a hitbox
		SURF_SKYNOEMIT = 0x10000,	// surface will show the skybox but does not emit light
		SURF_SKYOCCLUSION = 0x20000,    // surface will draw the skybox before any solids
		SURF_SLICK = 0x40000	// surface is zero friction
	}

	public struct InfoParm
	{
		public string name;
		public int clearSolid;
		public Q3SurfaceFlags surfaceFlags;
		public Q3ContentsFlags contents;

		public InfoParm(string name, int clearSolid, Q3SurfaceFlags surfaceFlags, Q3ContentsFlags contents)
		{
			this.name = name;
			this.clearSolid = clearSolid;
			this.surfaceFlags = surfaceFlags;
			this.contents = contents;
		}
	}

	public static class Constants
	{
		public static InfoParm[] infoParms =
		{
			// server relevant contents
			new InfoParm("water",       1,  0,  Q3ContentsFlags.CONTENTS_WATER ),
			new InfoParm("slime",       1,  0,  Q3ContentsFlags.CONTENTS_SLIME ),		// mildly damaging
			new InfoParm("lava",        1,  0,  Q3ContentsFlags.CONTENTS_LAVA ),		// very damaging
			new InfoParm("playerclip",  1,  0,  Q3ContentsFlags.CONTENTS_PLAYERCLIP ),
			new InfoParm("monsterclip", 1,  0,  Q3ContentsFlags.CONTENTS_MONSTERCLIP ),
			new InfoParm("nodrop",      1,  0,  Q3ContentsFlags.CONTENTS_NODROP ),		// don't drop items or leave bodies (death fog, lava, etc)
			new InfoParm("nonsolid",    1,  Q3SurfaceFlags.SURF_NONSOLID,  0),						// clears the solid flag

			// utility relevant attributes
			new InfoParm("origin",      1,  0,  Q3ContentsFlags.CONTENTS_ORIGIN ),		// center of rotating brushes
			new InfoParm("trans",       0,  0,  Q3ContentsFlags.CONTENTS_TRANSLUCENT ),	// don't eat contained surfaces
			new InfoParm("detail",      0,  0,  Q3ContentsFlags.CONTENTS_DETAIL ),		// don't include in structural bsp
			new InfoParm("structural",  0,  0,  Q3ContentsFlags.CONTENTS_STRUCTURAL ),	// force into structural bsp even if trnas
			new InfoParm("areaportal",  1,  0,  Q3ContentsFlags.CONTENTS_AREAPORTAL ),	// divides areas
			new InfoParm("clusterportal", 1,0,  Q3ContentsFlags.CONTENTS_CLUSTERPORTAL ),	// for bots
			new InfoParm("donotenter",  1,  0,  Q3ContentsFlags.CONTENTS_DONOTENTER ),		// for bots

			new InfoParm("fog",         1,  0,  Q3ContentsFlags.CONTENTS_FOG),			// carves surfaces entering
			new InfoParm("sky",         0,  Q3SurfaceFlags.SURF_SKY,       0 ),		// emit light from an environment map
			new InfoParm("lightfilter", 0,  Q3SurfaceFlags.SURF_LIGHTFILTER, 0 ),		// filter light going through it
			new InfoParm("alphashadow", 0,  Q3SurfaceFlags.SURF_ALPHASHADOW, 0 ),		// test light on a per-pixel basis
			new InfoParm("hint",        0,  Q3SurfaceFlags.SURF_HINT,      0 ),		// use as a primary splitter

			// server attributes
			new InfoParm("slick",       0,  Q3SurfaceFlags.SURF_SLICK,     0 ),
			new InfoParm("noimpact",    0,  Q3SurfaceFlags.SURF_NOIMPACT,  0 ),		// don't make impact explosions or marks
			new InfoParm("nomarks",     0,  Q3SurfaceFlags.SURF_NOMARKS,   0 ),		// don't make impact marks, but still explode
			new InfoParm("ladder",      0,  Q3SurfaceFlags.SURF_LADDER,    0 ),
			new InfoParm("nodamage",    0,  Q3SurfaceFlags.SURF_NODAMAGE,  0 ),
			new InfoParm("metalsteps",  0,  Q3SurfaceFlags.SURF_METALSTEPS,0 ),
			new InfoParm("flesh",       0,  Q3SurfaceFlags.SURF_FLESH,     0 ),
			new InfoParm("nosteps",     0,  Q3SurfaceFlags.SURF_NOSTEPS,   0 ),

			// drawsurf attributes
			new InfoParm("nodraw",      0,  Q3SurfaceFlags.SURF_NODRAW,    0 ),	// don't generate a drawsurface (or a lightmap)
			new InfoParm("pointlight",  0,  Q3SurfaceFlags.SURF_POINTLIGHT, 0 ),	// sample lighting at vertexes
			new InfoParm("nolightmap",  0,  Q3SurfaceFlags.SURF_NOLIGHTMAP,0 ),	// don't generate a lightmap
			new InfoParm("nodlight",    0,  Q3SurfaceFlags.SURF_NODLIGHT, 0 ),		// don't ever add dynamic lights
			new InfoParm("dust",        0,  Q3SurfaceFlags.SURF_DUST, 0)			// leave a dust trail when walking on this surface
		};
	}
}
