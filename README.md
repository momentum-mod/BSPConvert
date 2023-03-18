# BSP Convert
C# library for converting BSP files between engine versions without decompilation. This approach has the following advantages:
- Lightmaps can be preserved across engine versions (lighting information is lost when decompiling Quake BSP's)
- Maps can be ported in seconds with one command rather than many hours of manual effort

Check out this video for a demonstration of what this tool is currently capable of: https://www.youtube.com/watch?v=Tg6_sGcCLJ4

# BSP Convert Usage

Available command line arguments:
```
  --nopak                 Export materials into folders instead of embedding them in the BSP.

  --subdiv                (Default: 4) Displacement subdivisions [2-4].

  --mindmg                (Default: 50) Minimum damage to convert trigger_hurt into trigger_teleport.

  --prefix                Prefix for the converted BSP's file name.

  -o, --output            Output game directory for converted BSP/materials.

  --help                  Display this help screen.

  --version               Display version information.

  input files (pos. 0)    Required. Input Quake 3 BSP/PK3 file(s) to be converted.
```

Example usage:
`.\BSPConv.exe "C:\Users\<username>\Documents\BSPConvert\nood-aDr.pk3" --output "C:\Program Files (x86)\Steam\steamapps\common\Momentum Mod Playtest\momentum" --prefix "df_"`

List available command line options: `.\BSPConv.exe --help`

# Supported BSP conversions
- Quake 3 -> Source Engine **(WIP)**
- Half-Life (GoldSrc) -> Source Engine **(Not Started)**
