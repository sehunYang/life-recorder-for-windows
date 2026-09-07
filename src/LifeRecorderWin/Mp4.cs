namespace LifeRecorderWin;

/// <summary>
/// mp4 최상위 박스만 훑어 <c>moov</c> 가 있는지 본다.
///
/// ffmpeg 의 segment 먹서는 세그먼트를 닫을 때 <c>moov</c> 를 쓴다.
/// 따라서 <c>moov</c> 가 있으면 완성본, 없으면 쓰다가 죽은 파일이다.
/// 안드로이드도 같은 이유로 완성되지 않은 <c>screen_*.mp4.part</c> 를 복구하지 않고 지운다.
/// (ffprobe 를 부르지 않아도 되므로 ffmpeg.exe 하나만 번들하면 된다)
/// </summary>
internal static class Mp4
{
    public static bool HasMoov(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var header = new byte[16];
            long pos = 0;
            var length = fs.Length;

            while (pos + 8 <= length)
            {
                fs.Position = pos;
                if (!ReadExactly(fs, header, 8)) return false;

                long size = ((long)header[0] << 24) | ((long)header[1] << 16) | ((long)header[2] << 8) | header[3];
                var type = System.Text.Encoding.ASCII.GetString(header, 4, 4);
                var headerSize = 8;

                if (size == 1)
                {
                    // 64비트 크기. 박스 헤더 뒤에 8바이트가 더 붙는다.
                    if (!ReadExactly(fs, header, 8)) return false;
                    size = 0;
                    for (var i = 0; i < 8; i++) size = (size << 8) | header[i];
                    headerSize = 16;
                }
                else if (size == 0)
                {
                    // 파일 끝까지가 이 박스다.
                    size = length - pos;
                }

                if (type == "moov") return true;
                if (size < headerSize) return false;
                pos += size;
            }
            return false;
        }
        catch (IOException)
        {
            // 아직 ffmpeg 이 쓰고 있는 파일. 완성본이 아니다.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool ReadExactly(Stream s, byte[] buf, int count)
    {
        var read = 0;
        while (read < count)
        {
            var n = s.Read(buf, read, count - read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }
}
