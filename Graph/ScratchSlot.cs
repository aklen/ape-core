using System.Linq.Expressions;

namespace Ape.Core.Graph;

internal static class ScratchSlot
{
    public static string Name<TScratch, T>(Expression<Func<TScratch, T>> selector)
    {
        var parts = new List<string>();
        var body = Strip(selector.Body);
        while (body is MemberExpression member)
        {
            parts.Add(member.Member.Name);
            body = Strip(member.Expression);
        }

        if (body is ParameterExpression && parts.Count > 0)
        {
            parts.Reverse();
            return string.Join(".", parts);
        }

        throw new GraphCompileException(
            "CG212",
            "Port selector must be a scratch member access (e.g. s => s.Decode.Frames).");
    }

    private static Expression? Strip(Expression? expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
            expression = convert.Operand;
        return expression;
    }
}
