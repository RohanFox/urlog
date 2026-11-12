namespace Urlog.Core.Sources;

/// <summary>What a detector may know besides the bytes. Never read beyond <c>head</c> and <c>tail</c>.</summary>
public readonly record struct SniffContext(string FileName, long FileLength);
