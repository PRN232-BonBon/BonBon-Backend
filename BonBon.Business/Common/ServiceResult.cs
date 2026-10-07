namespace BonBon.Business.Common;

/// <summary>
/// Wrapper kết quả nghiệp vụ — tránh dùng exception cho các lỗi dự đoán được
/// (email đã tồn tại, sai mật khẩu, token hết hạn…).
/// </summary>
public sealed class ServiceResult<T>
{
    public bool IsSuccess { get; private init; }
    public T? Data { get; private init; }
    public string? ErrorCode { get; private init; }
    public string? ErrorMessage { get; private init; }

    public static ServiceResult<T> Success(T data) =>
        new() { IsSuccess = true, Data = data };

    public static ServiceResult<T> Failure(string errorCode, string errorMessage) =>
        new() { IsSuccess = false, ErrorCode = errorCode, ErrorMessage = errorMessage };
}

/// <summary>Non-generic variant cho các operation không trả data.</summary>
public sealed class ServiceResult
{
    public bool IsSuccess { get; private init; }
    public string? ErrorCode { get; private init; }
    public string? ErrorMessage { get; private init; }

    public static ServiceResult Success() =>
        new() { IsSuccess = true };

    public static ServiceResult Failure(string errorCode, string errorMessage) =>
        new() { IsSuccess = false, ErrorCode = errorCode, ErrorMessage = errorMessage };
}
