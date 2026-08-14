#if NETSTANDARD2_1
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }

    [System.AttributeUsage(System.AttributeTargets.All, AllowMultiple = true)]
    internal sealed class CompilerFeatureRequiredAttribute : System.Attribute
    {
        public string FeatureName { get; }
        public CompilerFeatureRequiredAttribute(string featureName) => FeatureName = featureName;
    }

    [System.AttributeUsage(System.AttributeTargets.All)]
    internal sealed class RequiredMemberAttribute : System.Attribute { }
}
namespace System.Diagnostics.CodeAnalysis
{
    [System.AttributeUsage(System.AttributeTargets.All)]
    internal sealed class SetsRequiredMembersAttribute : System.Attribute { }
}
#endif
