CustomContent folder
====================

Some Quake 3 / DeFRaG maps reference textures, shaders, or sounds that are NOT
bundled inside their own .pk3 - they live in other community maps' .pk3 files.
BSPConvert can't convert what it can't find, so those assets show up missing
(e.g. pink/checkerboard textures) in the converted map.

Drop those external dependencies in this folder and BSPConvert will search it
just like the Q3Content folder when resolving a map's assets.

How to use
----------
1. Find the map's dependencies. Map databases such as https://q3df.org list them
   under a "Map dependencies" / "Textures" section on each map's page. The names
   listed are the source maps that provide the missing assets.

2. Download those source maps (.pk3 files) and extract them (a .pk3 is just a
   .zip - rename it or open it with any archive tool).

3. Copy the extracted asset folders into THIS folder, preserving their structure:

       CustomContent/
         textures/<set>/...      (.tga / .jpg images)
         scripts/*.shader        (optional - preserves the original look)
         env/...                 (optional - skybox images)
         sound/...               (optional - custom sounds)

   You only need to copy the folders that contain the missing assets; copying an
   entire extracted map here also works.

4. Re-run BSPConvert. Assets found here are converted and embedded into the
   output BSP automatically.

Notes
-----
- Precedence: a map's own bundled assets and the Q3Content base assets always win;
  CustomContent is only used to fill in what's otherwise missing.
- Supported image formats: .tga and .jpg (same as Q3Content).
- This folder can hold the assets of many maps at once - just keep merging
  dependencies in as you convert more maps.
