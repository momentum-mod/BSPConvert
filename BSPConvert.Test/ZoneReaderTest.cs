using BSPConvert.Lib.Zones;

namespace BSPConvert.Test
{
	public class ZoneReaderTests
	{
		private string testFilePath;

		[SetUp]
		public void Setup()
		{
			testFilePath = Path.Combine(TestContext.CurrentContext.TestDirectory, "Test Files", "Zones", "test_timer_zones.json");
		}

		[Test]
		public void ReadFromFile_ValidJson_ReturnsZoneDefsBase()
		{
			var result = ZoneReader.ReadFromFile(testFilePath);

			Assert.That(result, Is.Not.Null);
		}

		[Test]
		public void ReadFromFile_ValidJson_ParsesTopLevelFields()
		{
			var result = ZoneReader.ReadFromFile(testFilePath);

			Assert.That(result.FormatVersion, Is.EqualTo(1));
			Assert.That(result.DataTimestamp, Is.EqualTo(1769799599L));
			Assert.That(result.MaxVelocity, Is.EqualTo(0.0f));
		}

		[Test]
		public void ReadFromFile_ValidJson_ParsesMainTrack()
		{
			var result = ZoneReader.ReadFromFile(testFilePath);

			Assert.That(result.Tracks.Main, Is.Not.Null);
			Assert.That(result.Tracks.Main.StagesEndAtStageStarts, Is.True);
			Assert.That(result.Tracks.Main.BhopEnabled, Is.False);
		}

		[Test]
		public void ReadFromFile_ValidJson_ParsesMainTrackSegments()
		{
			var result = ZoneReader.ReadFromFile(testFilePath);

			var segments = result.Tracks.Main.Zones.Segments;
			Assert.That(segments, Has.Count.EqualTo(5));

			var firstSegment = segments[0];
			Assert.That(firstSegment.LimitStartGroundSpeed, Is.True);
			Assert.That(firstSegment.CheckpointsRequired, Is.False);
			Assert.That(firstSegment.CheckpointsOrdered, Is.True);
			Assert.That(firstSegment.Name, Is.EqualTo("Test 1"));
		}

		[Test]
		public void ReadFromFile_ValidJson_ParsesSegmentCheckpointRegions()
		{
			var result = ZoneReader.ReadFromFile(testFilePath);

			var firstCheckpoint = result.Tracks.Main.Zones.Segments[0].Checkpoints[0];
			Assert.That(firstCheckpoint.Regions, Has.Count.EqualTo(1));

			var region = firstCheckpoint.Regions[0];
			Assert.That(region.Bottom, Is.EqualTo(0.0f));
			Assert.That(region.Height, Is.EqualTo(64.0f));
			Assert.That(region.TeleDestTargetname, Is.EqualTo("start"));
			Assert.That(region.Points, Has.Count.EqualTo(4));
			Assert.That(region.Points[0], Is.EqualTo(new float[] { -64.0f, 64.0f }));
		}

		[Test]
		public void ReadFromFile_ValidJson_ParsesSegmentWithMultipleCheckpointRegions()
		{
			var result = ZoneReader.ReadFromFile(testFilePath);

			// Test 4 (index 3) has 3 checkpoints, with the 2nd and 3rd having 2 regions each
			var segment = result.Tracks.Main.Zones.Segments[3];
			Assert.That(segment.Checkpoints, Has.Count.EqualTo(3));
			Assert.That(segment.Checkpoints[1].Regions, Has.Count.EqualTo(2));
			Assert.That(segment.Checkpoints[2].Regions, Has.Count.EqualTo(2));
		}

		[Test]
		public void ReadFromFile_ValidJson_ParsesMainTrackEndZone()
		{
			var result = ZoneReader.ReadFromFile(testFilePath);

			var endZone = result.Tracks.Main.Zones.End;
			Assert.That(endZone, Is.Not.Null);
			Assert.That(endZone.Regions, Has.Count.EqualTo(1));

			var region = endZone.Regions[0];
			Assert.That(region.Bottom, Is.EqualTo(0.0f));
			Assert.That(region.Height, Is.EqualTo(64.0f));
			Assert.That(region.Points[0], Is.EqualTo(new float[] { -64.0f, 256.0f }));
		}

		[Test]
		public void ReadFromFile_ValidJson_ParsesBonusTracks()
		{
			var result = ZoneReader.ReadFromFile(testFilePath);

			Assert.That(result.Tracks.Bonuses, Has.Count.EqualTo(1));

			var bonus = result.Tracks.Bonuses[0];
			Assert.That(bonus.BhopEnabled, Is.False);
			Assert.That(bonus.Zones.Segments, Has.Count.EqualTo(1));
			Assert.That(bonus.Zones.Segments[0].Checkpoints, Has.Count.EqualTo(2));
		}

		[Test]
		public void ReadFromFile_ValidJson_ParsesGlobalRegions()
		{
			var result = ZoneReader.ReadFromFile(testFilePath);

			Assert.That(result.GlobalRegions, Is.Not.Null);
			Assert.That(result.GlobalRegions.Cancel, Has.Count.EqualTo(1));

			var cancelRegion = result.GlobalRegions.Cancel[0];
			Assert.That(cancelRegion.Bottom, Is.EqualTo(0.0f));
			Assert.That(cancelRegion.Height, Is.EqualTo(64.0f));
			Assert.That(cancelRegion.Points[0], Is.EqualTo(new float[] { -160.0f, 224.0f }));
		}

		[Test]
		public void ReadFromFile_FileNotFound_ThrowsFileNotFoundException()
		{
			Assert.Throws<FileNotFoundException>(() =>
				ZoneReader.ReadFromFile("nonexistent/path/zones.json"));
		}
	}
}
