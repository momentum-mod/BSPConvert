# BSP Convert
C# tools and library for converting BSP files between engine versions without decompilation. This approach has the following advantages:
- Lightmaps can be preserved across engine versions (lighting information is lost when decompiling Quake BSP's)
- Maps can be ported in seconds with one command rather than many hours of manual effort

Check out this video for a demonstration of what this tool is currently capable of: https://www.youtube.com/watch?v=Tg6_sGcCLJ4

# BSP Convert Usage

```
.\BSPConv.exe [options] <input files>
```

The input files are Quake 3 BSP/PK3 or GoldSrc (Half-Life, CS 1.6) BSP/ZIP files. Archives (.pk3/.zip) convert every BSP they contain. The engine is detected from each BSP, and options for the other engine are ignored.

## General options
```
  --nopak                 Export materials (and GoldSrc sounds and models) into folders instead of embedding them in the BSP.
  --compress              LZMA compress the converted BSP.
  --prefix                Prefix for the converted BSP's file name.
  --output                Output game directory for converted BSP/materials. Defaults to the input file's folder.
  --maps                  Convert only the named BSP(s) from an archive instead of every BSP it contains. Comma-separated map names without extension (e.g. --maps pgrocket,pgplasma). Case-insensitive.
  --help                  Display this help screen.
  --version               Display version information.
```

## Quake 3 options
```
  --subdiv                (Default: 3) Displacement subdivisions [2-4].
  --notooldisps           Skip converting patches with tool textures to displacements.
  --patchdisps            Convert patches to displacements instead of primitive meshes (curved collision, no per-vertex UV/lightmap, not entity-attachable).
  --mindmg                (Default: 50) Minimum damage for trigger_hurt to respawn player.
  --lavatriggers          Duplicate lava brushes into trigger_hurt volumes that kill/respawn the player on contact.
  --nozones               Ignore timer zone triggers (no zone file is written).
  --offmodeents           (Default: cpm) For maps with different cpm/vq3 entities, choose which entities to use when played in non-defrag modes (cpm or vq3).
  --scale                 (Default: 1) Uniformly scale the map's geometry and entity positions by this factor (e.g. 2 doubles the world size). Mover speed/wait times, jump pad and teleport velocities, and other gameplay-tuning values are not scaled.
  --fogobb                Use legacy obb_volumefog entities instead of the default Fog shader. Handles arbitrary brush shapes but can flicker on thin volumes.
  --fogminheight          (Default: 256) Minimum vertical height (units) for converted fog volumes. Only used with --fogobb.
  --nofogoverlay          Don't draw fog shader visible stages (e.g. scrolling clouds) as an overlay face; fog brush faces are just dropped.
  --noenvmap              Disable envmap (specular cubemap) shader conversion. Useful for maps where Source's cubemap poorly emulates Quake 3's spheremap effect.
  --clampoverbright       Apply Quake 3's hue-preserving overbright clamp to lightmaps, flattening over-bright highlights toward white.
  --noanim                Disable baking multi-pass / animated shaders (scrolling liquids, layered effects, animMaps) into animated flipbook textures.
  --animbudget            (Default: 16) Target max size (MB) of each baked animated VTF. Resolution adapts down as the seamless loop needs more frames.
  --animmaxres            (Default: 512) Sharpness ceiling (px) for baked animated textures.
  --animfps               (Default: 16) Playback fps (smoothness) of baked animated textures.
  --animmaxframes         (Default: 512) Hard cap on baked frame count. Longer loops are sped up to fit while staying seamless.
  --animalpha             (Default: 1) Translucency [0-1] for baked liquids. 1 derives per-texel translucency from each texture's alpha/luminance; below 1 uses a flat constant alpha.
```

## GoldSrc options
```
  --wads                  Comma-separated folders to search (recursively) for the WAD files the map takes its textures from, its sky images (gfx/env), and the sounds, sprites and models its entities use, e.g. your Half-Life install or a folder of community WADs. The input's own folder and a default Steam Half-Life install are always searched.
  --studiomdl             Path to the studiomdl.exe that compiles the models entities use (in Momentum Mod's bin/win64 folder). Found next to the output game folder or in a default Steam Momentum Mod install if not given. Models aren't converted if it isn't found.
  --goldsrchulls          Copy the player clip hulls of this GoldSrc BSP into the input BSP(s) instead of converting them (see below).
```

GoldSrc maps don't get timer zones, and --scale isn't supported for them.

The conversion log says which cliptype the map was compiled with: legacy (all stock CS 1.6 maps and many KZ/bhop maps), or simple or precise.

### Recompiled maps
Converted maps carry the GoldSrc map's player collision (its clip hulls) as brushes of their own, which VBSP can't build. To keep that collision in a map that was recompiled with VBSP, e.g. after giving it new visuals, run the recompiled BSP through BSPConvert with `--goldsrchulls` and the original GoldSrc BSP:

`.\BSPConv.exe "C:\...\momentum\maps\kz_persia.bsp" --goldsrchulls "C:\...\Half-Life\cstrike\maps\kz_persia.bsp" --output "C:\...\output"`

GoldSrc-hull game modes then collide exactly as in the GoldSrc map, whatever changed visually, as long as the map wasn't moved. Brush entities (doors, trains, walls etc.) get their own collision when the recompiled map has an entity of the same name, or with the same bounds if it has none; the log lists GoldSrc entities nothing matched. Only the collision lumps and worldspawn change; run it on the finished BSP, after VVIS and VRAD.

## Examples
Quake 3:
`.\BSPConv.exe "C:\Users\<username>\Documents\BSPConvert\nood-aDr.pk3" --output "C:\Program Files (x86)\Steam\steamapps\common\Momentum Mod Playtest\momentum"`

GoldSrc:
`.\BSPConv.exe "C:\Program Files (x86)\Steam\steamapps\common\Half-Life\cstrike\maps\kz_persia.bsp" --wads "C:\Users\<username>\Documents\BSPConvert\wads" --output "C:\Program Files (x86)\Steam\steamapps\common\Momentum Mod Playtest\momentum"`

# Supported BSP conversions
- Quake 3 -> Strata Source **(Released)**: some shaders and entities don't convert exactly yet; please report problems on the issue tracker.
- Half-Life (GoldSrc) -> Source Engine **(WIP)**
