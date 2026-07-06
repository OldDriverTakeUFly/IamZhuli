// 仅 netstandard2.1 需要:提供 record init 所需的 IsExternalInit 占位类型
// net5+ 已内置此类型,用 #if 条件编译避免冲突
#if NETSTANDARD2_1
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif
