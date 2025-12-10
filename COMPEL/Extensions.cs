namespace COMPEL;

internal static class EXTENSIONS
{
    internal static int GetDeterministicHashCode(this object hashable)
    {
        byte[] hash = MD5.Create().ComputeHash(Encoding.UTF8.GetBytes(hashable.ToString() ?? string.Empty));

        int first = BitConverter.ToInt32(hash, 0);
        int second = BitConverter.ToInt32(hash, 4);
        int third = BitConverter.ToInt32(hash, 8);
        int fourth = BitConverter.ToInt32(hash, 12);

        return first ^ second ^ third ^ fourth;
    }
}
