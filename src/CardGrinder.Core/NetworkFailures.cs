using System.Net.Sockets;

namespace CardGrinder.Core;

public static class NetworkFailures
{
    // HTTP rejections, authentication failures and unrecognized badge data require user action.
    public static bool IsTransient(Exception exception) => exception switch
    {
        InvalidDataException => false,
        HttpRequestException request => request.StatusCode is null,
        TimeoutException or SocketException or IOException => true,
        OperationCanceledException { InnerException: TimeoutException } => true,
        _ => false
    };
}
