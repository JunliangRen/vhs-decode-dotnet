using System.Globalization;
using System.Text;

namespace VHSDecode.Preview;

public static class HlsPlaylistBuilder
{
    public static string BuildMaster(PreviewMediaInfo mediaInfo)
    {
        ArgumentNullException.ThrowIfNull(mediaInfo);
        return string.Join('\n',
            "#EXTM3U",
            "#EXT-X-VERSION:7",
            $"#EXT-X-STREAM-INF:BANDWIDTH=3000000,AVERAGE-BANDWIDTH=1600000,CODECS=\"avc1.4d401f\",RESOLUTION={mediaInfo.Width}x{mediaInfo.Height}",
            "index.m3u8",
            string.Empty);
    }

    public static string BuildMedia(PreviewTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        double maximumDuration = 0.0;
        for (int segment = 0; segment < timeline.SegmentCount; segment++)
        {
            maximumDuration = Math.Max(
                maximumDuration,
                timeline.SegmentDurationSeconds(segment));
        }

        var output = new StringBuilder();
        output.AppendLine("#EXTM3U");
        output.AppendLine("#EXT-X-VERSION:7");
        output.Append("#EXT-X-TARGETDURATION:");
        output.AppendLine(Math.Max(1, (int)Math.Ceiling(maximumDuration))
            .ToString(CultureInfo.InvariantCulture));
        output.AppendLine("#EXT-X-PLAYLIST-TYPE:VOD");
        output.AppendLine("#EXT-X-MEDIA-SEQUENCE:0");
        output.AppendLine("#EXT-X-INDEPENDENT-SEGMENTS");

        for (int window = 0; window < timeline.WindowCount; window++)
        {
            if (window > 0)
            {
                output.AppendLine("#EXT-X-DISCONTINUITY");
            }

            output.Append("#EXT-X-MAP:URI=\"");
            output.Append($"window/{window}/init.mp4");
            output.AppendLine("\"");
            int firstSegment = timeline.FirstSegmentInWindow(window);
            int segmentCount = timeline.SegmentCountInWindow(window);
            for (int local = 0; local < segmentCount; local++)
            {
                int global = firstSegment + local;
                output.Append("#EXTINF:");
                output.Append(timeline.SegmentDurationSeconds(global)
                    .ToString("0.000000", CultureInfo.InvariantCulture));
                output.AppendLine(",");
                output.AppendLine($"window/{window}/segment/{local}.m4s");
            }
        }

        output.AppendLine("#EXT-X-ENDLIST");
        return output.ToString();
    }
}
