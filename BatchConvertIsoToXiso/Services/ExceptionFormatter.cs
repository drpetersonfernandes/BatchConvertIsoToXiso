using System.Globalization;
using System.Text;

namespace BatchConvertIsoToXiso.Services;

public static class ExceptionFormatter
{
    public static void AppendExceptionDetails(StringBuilder sb, Exception exception, int level = 0)
    {
        while (true)
        {
            var indent = new string(' ', level * 2);

            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}Type: {exception.GetType().FullName}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}Message: {exception.Message}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}Source: {exception.Source}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}StackTrace:");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{exception.StackTrace}");

            if (exception is AggregateException aggregateException)
            {
                var innerExceptions = aggregateException.InnerExceptions;
                for (var i = 0; i < innerExceptions.Count; i++)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}Inner Exception [{i}]:");
                    AppendExceptionDetails(sb, innerExceptions[i], level + 1);
                }

                return;
            }

            if (exception.InnerException != null)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}Inner Exception:");
                exception = exception.InnerException;
                level += 1;
                continue;
            }

            break;
        }
    }
}