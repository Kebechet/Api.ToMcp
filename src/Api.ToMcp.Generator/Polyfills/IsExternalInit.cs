namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// Lets the compiler emit <c>init</c> accessors, which records use, for netstandard2.0, whose base library lacks this
    /// type. The compiler looks it up by name only.
    /// </summary>
    internal static class IsExternalInit
    {
    }
}
