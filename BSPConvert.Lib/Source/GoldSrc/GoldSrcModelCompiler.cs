using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

namespace BSPConvert.Lib.GoldSrc
{
	// Compiles GoldSrc studio models into Source models with the game's studiomdl. Each model is written as SMDs and a
	// QC and compiled into a game folder of its own under the work directory, so nothing is written to the game. Its
	// textures become materials under materials/models/goldsrc/<map>/<model>/.
	//
	// GoldSrc's studiomdl rotated the models by 90 degrees about z when they were compiled, which Source's studiomdl
	// would do again, so the QC turns its rotation off ($origin).
	public class GoldSrcModelCompiler
	{
		// Frame rate of the sequences that hold a single frame
		private const float StillFps = 30f;
		// Frames per turn of a spinning sequence (see GetSpinningSequence), 5 degrees apart
		private const int SpinFrames = 72;
		// How long a model may take to compile before studiomdl is assumed to be stuck
		private const int StudiomdlTimeoutMs = 120000;

		private readonly string studiomdlPath;
		private readonly string workDir;
		private readonly string gameDir;
		// The map's asset folder ("goldsrc/<map>"), which its models go in under models/
		private readonly string assetDir;
		private readonly GoldSrcMaterialConverter materialConverter;
		private readonly ILogger logger;
		private readonly Dictionary<string, ModelEntry> models = new Dictionary<string, ModelEntry>(StringComparer.OrdinalIgnoreCase);
		private readonly List<(string path, string file)> compiledFiles = new List<(string, string)>();

		// A sequence played the way an entity plays it: at a multiple of its frame rate, or holding one frame, which may
		// spin about the model's z axis at SpinSpeed degrees per second
		private record SequenceVariant(int Sequence, float Fps, int? StillFrame, float SpinSpeed = 0f);

		private class ModelEntry
		{
			public GoldSrcModel Model = null!;
			public string[] SequenceNames = Array.Empty<string>();
			public Dictionary<string, SequenceVariant> Variants = new Dictionary<string, SequenceVariant>(StringComparer.OrdinalIgnoreCase);
			// The box entities with SOLID_BBOX collide with, or null for the model's own
			public (Vector3 Mins, Vector3 Maxs)? CollisionBox;
		}

		public GoldSrcModelCompiler(string studiomdlPath, string workDir, string assetDir, GoldSrcMaterialConverter materialConverter, ILogger logger)
		{
			this.studiomdlPath = studiomdlPath;
			this.workDir = workDir;
			this.assetDir = assetDir;
			this.materialConverter = materialConverter;
			this.logger = logger;

			gameDir = Path.Combine(workDir, "game");
			Directory.CreateDirectory(gameDir);
			File.WriteAllText(Path.Combine(gameDir, "gameinfo.txt"),
				"\"GameInfo\"\n{\n\tgame \"BSPConvert\"\n\tFileSystem\n\t{\n\t\tSearchPaths\n\t\t{\n\t\t\tgame |gameinfo_path|.\n\t\t}\n\t}\n}\n");
		}

		// The compiled models' files: their paths in the game ("models/goldsrc/<map>/x.mdl") and where they are
		public IReadOnlyList<(string path, string file)> CompiledFiles => compiledFiles;

		// Adds a model by its path in the mod ("models/x.mdl"), returning its path in the game ("models/goldsrc/<map>/x.mdl")
		public string AddModel(string modelPath, GoldSrcModel model)
		{
			var name = GetSourceModelName(modelPath);
			if (!models.ContainsKey(name))
			{
				models[name] = new ModelEntry
				{
					Model = model,
					SequenceNames = MakeUnique(model.Sequences.Select(sequence => SanitizeName(sequence.Name, "sequence"))),
				};
			}

			return name;
		}

		// "models/FLasH/Grass5.mdl" -> "models/goldsrc/<map>/flash/grass5.mdl"
		private string GetSourceModelName(string modelPath)
		{
			var path = modelPath.ToLowerInvariant();
			if (path.StartsWith("models/", StringComparison.Ordinal))
				path = path.Substring("models/".Length);

			var parts = Path.ChangeExtension(path, null).Split('/', StringSplitOptions.RemoveEmptyEntries).Select(part => SanitizeName(part, "model"));
			return $"models/{assetDir}/{string.Join('/', parts)}.mdl";
		}

		// The sequence an entity plays a model's sequence with when GoldSrc's client animates it at framerate times the
		// sequence's frame rate from the start of the map (StudioEstimateFrame), as it does for entities that don't
		// animate themselves. A sequence that doesn't loop soon stops on its last frame, so it holds that frame from
		// the start. Returns the name of the sequence the Source model has for it.
		// TODO: Negative frame rates, which play looping sequences backwards
		public string GetSequence(string sourceModel, int sequence, float framerate)
		{
			var entry = models[sourceModel];
			if (sequence < 0 || sequence >= entry.Model.Sequences.Count)
				sequence = 0;

			var gsSequence = entry.Model.Sequences[sequence];
			var name = entry.SequenceNames[sequence];
			if (gsSequence.FrameCount <= 1)
				return name;

			if (framerate == 0f || gsSequence.Fps <= 0f || !gsSequence.Looping)
			{
				var frame = framerate > 0f && gsSequence.Fps > 0f ? gsSequence.FrameCount - 1 : 0;
				return AddVariant(entry, $"{name}_frame{frame}", new SequenceVariant(sequence, StillFps, frame));
			}

			var rate = MathF.Abs(framerate);
			if (rate == 1f)
				return name;

			var rateName = rate.ToString("0.##", CultureInfo.InvariantCulture).Replace('.', 'p');
			return AddVariant(entry, $"{name}_x{rateName}", new SequenceVariant(sequence, gsSequence.Fps * rate, null));
		}

		// Sets the box a model collides with as an entity with SOLID_BBOX ($bbox), in place of its bounds. It doesn't
		// change how it's drawn.
		public void SetCollisionBox(string sourceModel, Vector3 mins, Vector3 maxs)
		{
			models[sourceModel].CollisionBox = (mins, maxs);
		}

		// The sequence an entity plays when it holds the first frame of a model's sequence while spinning about the
		// model's z axis at speed degrees per second (anticlockwise seen from above), as a func_train drawing a model
		// turns by its avelocity. Returns the name of the sequence the Source model has for it.
		public string GetSpinningSequence(string sourceModel, int sequence, float speed)
		{
			var entry = models[sourceModel];
			if (sequence < 0 || sequence >= entry.Model.Sequences.Count)
				sequence = 0;

			var speedName = MathF.Abs(speed).ToString("0.##", CultureInfo.InvariantCulture).Replace('.', 'p');
			var name = $"{entry.SequenceNames[sequence]}_spin{(speed < 0f ? "cw" : "")}{speedName}";
			return AddVariant(entry, name, new SequenceVariant(sequence, SpinFrames * MathF.Abs(speed) / 360f, 0, speed));
		}

		private static string AddVariant(ModelEntry entry, string name, SequenceVariant variant)
		{
			// A variant's name can't be taken by a sequence of the model
			while (entry.SequenceNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
				(entry.Variants.TryGetValue(name, out var existing) && existing != variant))
				name += "_";

			entry.Variants[name] = variant;
			return name;
		}

		// Compiles a model added with AddModel, returning false if it failed
		public bool Compile(string sourceModel)
		{
			var entry = models[sourceModel];
			var model = entry.Model;
			var modelName = Path.ChangeExtension(sourceModel.Substring("models/".Length), null);
			var srcDir = Path.Combine(workDir, "src", modelName.Replace('/', Path.DirectorySeparatorChar));
			Directory.CreateDirectory(srcDir);

			var materialDir = "models/" + modelName;
			// studiomdl strips anything after a dot from material names as an extension ("trees_leaves.512.bmp" would
			// look for trees_leaves.vmt), so they have none
			var materialNames = MakeUnique(model.Textures.Select(texture => SanitizeName(Path.GetFileNameWithoutExtension(texture.Name).Replace('.', '_'), "texture")));
			for (var i = 0; i < model.Textures.Count; i++)
			{
				if (!materialConverter.ConvertModelTexture(materialDir + "/" + materialNames[i], model.Textures[i]))
				{
					logger.Log($"Warning: Couldn't convert texture {model.Textures[i].Name} of {sourceModel}");
					return false;
				}
			}

			var boneNames = MakeUnique(model.Bones.Select(bone => SanitizeName(bone.Name, "bone")));
			var qc = new StringBuilder();
			qc.AppendLine(CultureInfo.InvariantCulture, $"$modelname \"{modelName}.mdl\"");
			qc.AppendLine(CultureInfo.InvariantCulture, $"$cdmaterials \"{materialDir}/\"");
			qc.AppendLine("$origin 0 0 0 -90");
			qc.AppendLine("$surfaceprop \"default\"");
			if (entry.CollisionBox is var (mins, maxs))
				qc.AppendLine(CultureInfo.InvariantCulture, $"$bbox {Format(mins)} {Format(maxs)}");

			var hasTriangles = false;
			for (var i = 0; i < model.BodyParts.Count; i++)
			{
				var bodyPart = model.BodyParts[i];
				qc.AppendLine(CultureInfo.InvariantCulture, $"$bodygroup \"{SanitizeName(bodyPart.Name, "body")}\"");
				qc.AppendLine("{");
				for (var j = 0; j < bodyPart.Models.Count; j++)
				{
					var subModel = bodyPart.Models[j];
					if (subModel.Triangles.Count == 0)
					{
						qc.AppendLine("\tblank");
						continue;
					}

					var smdName = $"body{i}_{j}.smd";
					WriteReferenceSmd(Path.Combine(srcDir, smdName), model, boneNames, subModel, materialNames);
					qc.AppendLine(CultureInfo.InvariantCulture, $"\tstudio \"{smdName}\"");
					hasTriangles = true;
				}
				qc.AppendLine("}");
			}

			if (!hasTriangles)
			{
				logger.Log($"Warning: {sourceModel} has no triangles");
				return false;
			}

			WriteTextureGroup(qc, model, materialNames);

			for (var i = 0; i < model.Sequences.Count; i++)
			{
				var sequence = model.Sequences[i];
				var smdName = $"sequence{i}.smd";
				WriteAnimationSmd(Path.Combine(srcDir, smdName), model, boneNames, sequence, 0, sequence.FrameCount);
				var fps = sequence.Fps > 0f ? sequence.Fps : StillFps;
				qc.AppendLine(CultureInfo.InvariantCulture, $"$sequence \"{entry.SequenceNames[i]}\" \"{smdName}\" fps {fps:0.###}{(sequence.Looping ? " loop" : "")}");
			}

			foreach (var (name, variant) in entry.Variants)
			{
				var sequence = model.Sequences[variant.Sequence];
				if (variant.SpinSpeed != 0f)
				{
					var smdName = name + ".smd";
					WriteSpinningSmd(Path.Combine(srcDir, smdName), model, boneNames, sequence, variant.StillFrame ?? 0, variant.SpinSpeed);
					qc.AppendLine(CultureInfo.InvariantCulture, $"$sequence \"{name}\" \"{smdName}\" fps {variant.Fps:0.######} loop");
				}
				else if (variant.StillFrame is int frame)
				{
					var smdName = $"sequence{variant.Sequence}_frame{frame}.smd";
					WriteAnimationSmd(Path.Combine(srcDir, smdName), model, boneNames, sequence, frame, 1);
					qc.AppendLine(CultureInfo.InvariantCulture, $"$sequence \"{name}\" \"{smdName}\" fps {variant.Fps:0.###} loop");
				}
				else
				{
					qc.AppendLine(CultureInfo.InvariantCulture, $"$sequence \"{name}\" \"sequence{variant.Sequence}.smd\" fps {variant.Fps:0.###} loop");
				}
			}

			var qcPath = Path.Combine(srcDir, "model.qc");
			File.WriteAllText(qcPath, qc.ToString());
			if (!RunStudiomdl(sourceModel, qcPath))
				return false;

			foreach (var extension in new[] { ".mdl", ".vvd", ".dx90.vtx" })
			{
				var path = Path.ChangeExtension(sourceModel, null) + extension;
				var file = Path.Combine(gameDir, path.Replace('/', Path.DirectorySeparatorChar));
				if (!File.Exists(file))
				{
					logger.Log($"Warning: studiomdl didn't write {path}");
					return false;
				}

				compiledFiles.Add((path, file));
			}

			return true;
		}

		// Skin families become a texture group of the textures that differ between them
		private static void WriteTextureGroup(StringBuilder qc, GoldSrcModel model, string[] materialNames)
		{
			if (model.SkinFamilies.Length < 2)
				return;

			var families = model.SkinFamilies;
			var skinRefs = Enumerable.Range(0, families[0].Length)
				.Where(skinRef => families.Any(family => family[skinRef] != families[0][skinRef]))
				.ToList();
			if (skinRefs.Count == 0)
				return;

			qc.AppendLine("$texturegroup \"skinfamilies\"");
			qc.AppendLine("{");
			foreach (var family in families)
			{
				var names = skinRefs.Select(skinRef => family[skinRef] >= 0 && family[skinRef] < materialNames.Length ? materialNames[family[skinRef]] : materialNames[families[0][skinRef]]);
				qc.AppendLine("\t{ " + string.Join(' ', names.Select(name => $"\"{name}\"")) + " }");
			}
			qc.AppendLine("}");
		}

		private bool RunStudiomdl(string sourceModel, string qcPath)
		{
			var startInfo = new ProcessStartInfo(studiomdlPath)
			{
				WorkingDirectory = Path.GetDirectoryName(qcPath)!,
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};
			startInfo.ArgumentList.Add("-nop4");
			startInfo.ArgumentList.Add("-game");
			startInfo.ArgumentList.Add(gameDir);
			startInfo.ArgumentList.Add(qcPath);

			using var process = Process.Start(startInfo);
			if (process == null)
			{
				logger.Log($"Warning: Couldn't start {studiomdlPath}");
				return false;
			}

			// Read both streams at once so neither fills up and blocks studiomdl
			var outputTask = process.StandardOutput.ReadToEndAsync();
			var errorTask = process.StandardError.ReadToEndAsync();
			if (!process.WaitForExit(StudiomdlTimeoutMs))
			{
				process.Kill(true);
				logger.Log($"Warning: studiomdl took too long to compile {sourceModel}");
				return false;
			}

			if (process.ExitCode == 0)
				return true;

			var lines = (outputTask.Result + errorTask.Result).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			var errorLines = lines.Where(line => line.Contains("ERROR", StringComparison.OrdinalIgnoreCase)).ToList();
			logger.Log($"Warning: studiomdl failed to compile {sourceModel}: {string.Join(" ", errorLines.Count > 0 ? errorLines : lines.TakeLast(3))}");
			return false;
		}

		private static void WriteReferenceSmd(string path, GoldSrcModel model, string[] boneNames, GoldSrcModel.SubModel subModel, string[] materialNames)
		{
			var smd = new StringBuilder();
			WriteSkeleton(smd, model, boneNames, new[] { model.Bones.Select(bone => bone.Position).ToArray() }, new[] { model.Bones.Select(bone => bone.Rotation).ToArray() });

			smd.AppendLine("triangles");
			foreach (var triangle in subModel.Triangles)
			{
				var texture = model.SkinFamilies.Length > 0 && triangle.SkinRef >= 0 && triangle.SkinRef < model.SkinFamilies[0].Length
					? model.SkinFamilies[0][triangle.SkinRef]
					: triangle.SkinRef;
				smd.AppendLine(texture >= 0 && texture < materialNames.Length ? materialNames[texture] : "missing");
				foreach (var vertex in new[] { triangle.A, triangle.B, triangle.C })
				{
					smd.AppendLine(CultureInfo.InvariantCulture,
						$"{vertex.Bone} {Format(vertex.Position)} {Format(vertex.Normal)} {vertex.TexCoord.X:0.######} {vertex.TexCoord.Y:0.######}");
				}
			}
			smd.AppendLine("end");

			File.WriteAllText(path, smd.ToString());
		}

		private static void WriteAnimationSmd(string path, GoldSrcModel model, string[] boneNames, GoldSrcModel.Sequence sequence, int firstFrame, int frameCount)
		{
			var smd = new StringBuilder();
			WriteSkeleton(smd, model, boneNames, sequence.Positions.Skip(firstFrame).Take(frameCount).ToArray(),
				sequence.Rotations.Skip(firstFrame).Take(frameCount).ToArray());
			File.WriteAllText(path, smd.ToString());
		}

		// A frame of a sequence turning once about the model's z axis, ending where it starts so it loops. The root
		// bones turn about the origin: SMD rotations apply about x, then y, then z, so turning about z adds to the last.
		private static void WriteSpinningSmd(string path, GoldSrcModel model, string[] boneNames, GoldSrcModel.Sequence sequence, int frame, float speed)
		{
			var positions = new Vector3[SpinFrames + 1][];
			var rotations = new Vector3[SpinFrames + 1][];
			for (var i = 0; i <= SpinFrames; i++)
			{
				var angle = MathF.CopySign(2f * MathF.PI * i / SpinFrames, speed);
				var (sin, cos) = MathF.SinCos(angle);
				positions[i] = (Vector3[])sequence.Positions[frame].Clone();
				rotations[i] = (Vector3[])sequence.Rotations[frame].Clone();
				for (var bone = 0; bone < model.Bones.Count; bone++)
				{
					if (model.Bones[bone].Parent >= 0)
						continue;

					var position = positions[i][bone];
					positions[i][bone] = new Vector3(position.X * cos - position.Y * sin, position.X * sin + position.Y * cos, position.Z);
					rotations[i][bone].Z += angle;
				}
			}

			var smd = new StringBuilder();
			WriteSkeleton(smd, model, boneNames, positions, rotations);
			File.WriteAllText(path, smd.ToString());
		}

		private static void WriteSkeleton(StringBuilder smd, GoldSrcModel model, string[] boneNames, Vector3[][] positions, Vector3[][] rotations)
		{
			smd.AppendLine("version 1");
			smd.AppendLine("nodes");
			for (var i = 0; i < model.Bones.Count; i++)
				smd.AppendLine(CultureInfo.InvariantCulture, $"{i} \"{boneNames[i]}\" {model.Bones[i].Parent}");
			smd.AppendLine("end");

			smd.AppendLine("skeleton");
			for (var frame = 0; frame < positions.Length; frame++)
			{
				smd.AppendLine(CultureInfo.InvariantCulture, $"time {frame}");
				for (var i = 0; i < model.Bones.Count; i++)
					smd.AppendLine(CultureInfo.InvariantCulture, $"{i} {Format(positions[frame][i])} {Format(rotations[frame][i])}");
			}
			smd.AppendLine("end");
		}

		private static string Format(Vector3 v)
		{
			return string.Create(CultureInfo.InvariantCulture, $"{v.X:0.######} {v.Y:0.######} {v.Z:0.######}");
		}

		// Names that are safe in a QC, an SMD and a file name
		private static string SanitizeName(string name, string fallback)
		{
			var sanitized = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_').ToArray()).Trim('.');
			return sanitized.Length > 0 ? sanitized : fallback;
		}

		// Appends a number to names that are already taken (case insensitively)
		private static string[] MakeUnique(IEnumerable<string> names)
		{
			var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			return names.Select(name =>
			{
				var unique = name;
				for (var i = 1; !used.Add(unique); i++)
					unique = $"{name}_{i}";

				return unique;
			}).ToArray();
		}
	}
}
