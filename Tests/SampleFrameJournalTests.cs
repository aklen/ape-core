using Ape.Core.Determinism;

namespace Ape.Core.Tests;

public sealed class SampleFrameJournalTests
{
    [Fact]
    public void Journal_round_trips_header_ordered_frames_and_binary_payload()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ape-sample-{Guid.NewGuid():N}.jsonl");
        try
        {
            var header = new SampleFrameJournalHeader(
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                TimeSpan.FromMilliseconds(100), "UTC", "config-v1");
            var first = new SampleFrame(7, header.LogicalEpoch, [
                new SampleFrameInput("serial-1", 12, null, "ubx-bytes", "v1", new byte[] { 0, 181, 98, 255 },
                    SourceEpoch: "connection-1")]);
            var second = new SampleFrame(8, header.LogicalEpoch.AddMilliseconds(100), []);

            using (var writer = SampleFrameJournal.CreateNew(path, header))
            {
                writer.Append(first);
                writer.Append(second);
                Assert.Throws<InvalidDataException>(() => writer.Append(first));
            }

            var (actualHeader, frames) = SampleFrameJournal.Read(path);
            Assert.Equal(header, actualHeader);
            Assert.Equal([7L, 8L], frames.Select(f => f.FrameId).ToArray());
            Assert.Equal(new byte[] { 0, 181, 98, 255 }, frames[0].Inputs[0].Payload!.Value.ToArray());
            Assert.Equal("connection-1", frames[0].Inputs[0].SourceEpoch);
            Assert.Empty(frames[1].Inputs);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Reader_rejects_truncated_frame_row()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ape-sample-{Guid.NewGuid():N}.jsonl");
        try
        {
            using (SampleFrameJournal.CreateNew(path,
                new SampleFrameJournalHeader(DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1)))) { }
            File.AppendAllText(path, "{\"Version\":1,\"Kind\":\"frame\",\"Frame\":");

            Assert.Throws<InvalidDataException>(() => SampleFrameJournal.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
