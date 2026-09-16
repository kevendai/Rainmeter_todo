using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace RainmeterBackend
{
    // 手写 ZIP 读取器。项目以 .NET Framework 4.0 为目标，而 System.IO.Compression 里的
    // ZipArchive / ZipFile 需要 4.5，所以这里只用 System.dll 的 DeflateStream，
    // 自己解析中央目录（CRC 校验、路径越界、符号链接、ZIP64、重复条目都在这一层拦掉）。
    // 更新器解压更新包、插件安装器解压 .rwplugin 包共用这一份实现，限制由调用方给出。
    internal sealed class ZipEntryInfo
    {
        public string Name; public ushort Method, Flags; public uint Crc, Compressed, Uncompressed, LocalOffset, External;
    }

    internal static class ZipArchiveReader
    {
        public static void Extract(string archive, string destination, long maxArchiveBytes, long maxEntryBytes, long maxExpandedBytes, int maxEntries)
        {
            if (new FileInfo(archive).Length > maxArchiveBytes) throw new InvalidDataException("压缩包超过大小限制。");
            Directory.CreateDirectory(destination);
            using (FileStream stream = File.OpenRead(archive))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                List<ZipEntryInfo> entries = ReadCentralDirectory(stream, reader, maxArchiveBytes, maxEntries);
                HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
                foreach (ZipEntryInfo entry in entries)
                {
                    string normalized = entry.Name.Replace('/', '\\'); bool directory = normalized.EndsWith("\\", StringComparison.Ordinal);
                    if (!names.Add(normalized.TrimEnd('\\'))) throw new InvalidDataException("压缩包包含重复路径：" + entry.Name);
                    if ((entry.Flags & 1) != 0 || (entry.Method != 0 && entry.Method != 8)) throw new InvalidDataException("压缩包使用了不支持的加密或压缩方式。");
                    if (entry.Uncompressed > maxEntryBytes || (total += entry.Uncompressed) > maxExpandedBytes) throw new InvalidDataException("解压后文件超过大小限制。");
                    if (((entry.External >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("压缩包不允许符号链接。");
                    string target = SafeDestination(destination, normalized);
                    if (directory) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    stream.Position = entry.LocalOffset;
                    if (reader.ReadUInt32() != 0x04034b50) throw new InvalidDataException("ZIP 本地文件头无效。");
                    stream.Position += 22; ushort nameLength = reader.ReadUInt16(), extraLength = reader.ReadUInt16(); stream.Position += nameLength + extraLength;
                    using (ZipBoundedStream bounded = new ZipBoundedStream(stream, entry.Compressed))
                    using (Stream input = entry.Method == 8 ? (Stream)new DeflateStream(bounded, CompressionMode.Decompress, true) : bounded)
                    using (FileStream output = File.Create(target))
                    {
                        ZipCrc32 crc = new ZipCrc32(); byte[] buffer = new byte[81920]; long written = 0; int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0) { written += read; if (written > entry.Uncompressed) throw new InvalidDataException("ZIP 条目长度无效。"); crc.Update(buffer, 0, read); output.Write(buffer, 0, read); }
                        if (written != entry.Uncompressed || crc.Value != entry.Crc) throw new InvalidDataException("ZIP 条目校验失败：" + entry.Name);
                    }
                }
            }
        }

        private static List<ZipEntryInfo> ReadCentralDirectory(FileStream stream, BinaryReader reader, long maxArchiveBytes, int maxEntries)
        {
            long search = Math.Min(stream.Length, 65557); stream.Position = stream.Length - search; byte[] tail = reader.ReadBytes((int)search); int eocd = -1;
            for (int i = tail.Length - 22; i >= 0; i--) if (BitConverter.ToUInt32(tail, i) == 0x06054b50) { eocd = i; break; }
            if (eocd < 0) throw new InvalidDataException("ZIP 中央目录缺失。");
            ushort count = BitConverter.ToUInt16(tail, eocd + 10), comment = BitConverter.ToUInt16(tail, eocd + 20); uint size = BitConverter.ToUInt32(tail, eocd + 12), offset = BitConverter.ToUInt32(tail, eocd + 16);
            if (count > maxEntries || eocd + 22 + comment > tail.Length || (long)offset + size > stream.Length) throw new InvalidDataException("ZIP 中央目录无效或过大。");
            stream.Position = offset; List<ZipEntryInfo> entries = new List<ZipEntryInfo>(count);
            for (int i = 0; i < count; i++)
            {
                if (reader.ReadUInt32() != 0x02014b50) throw new InvalidDataException("ZIP 中央目录条目无效。");
                reader.ReadUInt16(); reader.ReadUInt16(); ushort flags = reader.ReadUInt16(), method = reader.ReadUInt16(); stream.Position += 4; uint crc = reader.ReadUInt32(), compressed = reader.ReadUInt32(), uncompressed = reader.ReadUInt32(); ushort nameLength = reader.ReadUInt16(), extraLength = reader.ReadUInt16(), commentLength = reader.ReadUInt16(); stream.Position += 4; uint external = reader.ReadUInt32(), local = reader.ReadUInt32(); byte[] nameBytes = reader.ReadBytes(nameLength); Encoding encoding = (flags & 0x800) != 0 ? Encoding.UTF8 : Encoding.GetEncoding(437); string name = encoding.GetString(nameBytes); stream.Position += extraLength + commentLength;
                if (compressed == UInt32.MaxValue || uncompressed == UInt32.MaxValue || local == UInt32.MaxValue) throw new InvalidDataException("不支持 ZIP64 压缩包。");
                entries.Add(new ZipEntryInfo { Name = name, Method = method, Flags = flags, Crc = crc, Compressed = compressed, Uncompressed = uncompressed, LocalOffset = local, External = external });
            }
            return entries;
        }

        public static string FullDirectory(string path) { return Path.GetFullPath(path).TrimEnd('\\') + "\\"; }

        public static string SafeDestination(string root, string relative)
        {
            relative = (relative ?? "").Replace('/', '\\');
            if (relative.Length == 0 || Path.IsPathRooted(relative) || relative.Split('\\').Any(x => x == "..")) throw new InvalidDataException("压缩包路径无效。");
            string result = Path.GetFullPath(Path.Combine(root, relative));
            if (!result.StartsWith(FullDirectory(root), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("压缩包路径越界。");
            return result;
        }
    }

    internal sealed class ZipBoundedStream : Stream
    {
        private readonly Stream inner; private long remaining; public ZipBoundedStream(Stream inner, long length) { this.inner = inner; remaining = length; }
        public override int Read(byte[] buffer, int offset, int count) { if (remaining <= 0) return 0; int read = inner.Read(buffer, offset, (int)Math.Min(count, remaining)); remaining -= read; return read; }
        public override bool CanRead { get { return true; } } public override bool CanSeek { get { return false; } } public override bool CanWrite { get { return false; } } public override long Length { get { throw new NotSupportedException(); } } public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } } public override void Flush() { } public override long Seek(long o, SeekOrigin so) { throw new NotSupportedException(); } public override void SetLength(long v) { throw new NotSupportedException(); } public override void Write(byte[] b, int o, int c) { throw new NotSupportedException(); }
    }

    internal sealed class ZipCrc32
    {
        private uint crc = 0xffffffff; private static readonly uint[] Table = Build(); public uint Value { get { return crc ^ 0xffffffff; } }
        public void Update(byte[] bytes, int offset, int count) { for (int i = offset; i < offset + count; i++) crc = Table[(crc ^ bytes[i]) & 0xff] ^ (crc >> 8); }
        private static uint[] Build() { uint[] table = new uint[256]; for (uint i = 0; i < table.Length; i++) { uint value = i; for (int j = 0; j < 8; j++) value = (value & 1) != 0 ? 0xedb88320 ^ (value >> 1) : value >> 1; table[i] = value; } return table; }
    }
}
