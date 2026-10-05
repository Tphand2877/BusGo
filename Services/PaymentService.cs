using System.Data;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using BusGo.Data;
using BusGo.Models;
using Microsoft.Data.SqlClient;
using static BusGo.Services.UiText;

namespace BusGo.Services;

public sealed record PaymentRequest(int TicketId, string TicketCode, string PaymentMethod = "BankTransfer");
public sealed record PaymentResult(bool IsSuccess, int PaymentId = 0, string? TransactionNo = null, string? PaymentUrl = null, string? ErrorMessage = null);
public sealed record PaymentVerificationResult(bool IsSuccess, bool AlreadyProcessed, string? ErrorMessage = null);

public static class PaymentService
{
    private static readonly Uri VnPayEndpoint = new("https://sandbox.vnpayment.vn/paymentv2/vpcpay.html");

    /// <summary>True when both the VNPay merchant code and hash secret are configured on this machine.</summary>
    public static bool IsVnPayConfigured =>
        !string.IsNullOrWhiteSpace(AppConfig.VnPayTmnCode) && !string.IsNullOrWhiteSpace(AppConfig.VnPayHashSecret);

    public static async Task<PaymentResult> CreatePaymentAsync(PaymentRequest request)
    {
        if (request.TicketId <= 0 || string.IsNullOrWhiteSpace(request.TicketCode) || string.IsNullOrWhiteSpace(request.PaymentMethod))
            return new(false, ErrorMessage: T("Thông tin yêu cầu thanh toán chưa đầy đủ.", "Payment request is incomplete."));

        var method = NormalizeMethod(request.PaymentMethod);
        if (method is null)
            return new(false, ErrorMessage: Format("'{0}' không phải phương thức thanh toán được hỗ trợ. Hãy dùng VNPay, chuyển khoản hoặc tiền mặt.", "'{0}' is not a supported payment method. Use VNPay, BankTransfer or Cash.", request.PaymentMethod));

        try
        {
            await ExpirePendingPaymentsAsync();
        }
        catch (SqlException ex)
        {
            LoggerService.LogError("Could not expire pending payments before creating a payment.", ex);
            return new(false, ErrorMessage: T("Không thể truy cập bảng thanh toán. Hãy chạy bản cập nhật cơ sở dữ liệu rồi thử lại.", "The payment tables are unreachable. Run the database migration and try again."));
        }

        return method switch
        {
            "Cash" => await CreateCashPaymentAsync(request),
            "BankTransfer" => await CreateBankTransferPaymentAsync(request),
            _ => await CreateVnPayPaymentAsync(request)
        };
    }

    private static string? NormalizeMethod(string method) =>
        method.Trim() switch
        {
            var m when m.Equals("VNPay", StringComparison.OrdinalIgnoreCase) => "VNPay",
            var m when m.Equals("BankTransfer", StringComparison.OrdinalIgnoreCase) => "BankTransfer",
            var m when m.Equals("Cash", StringComparison.OrdinalIgnoreCase) => "Cash",
            _ => null
        };

    private const string PayableTicketGuard = @"
DECLARE @Amount decimal(18,2);
SELECT @Amount=t.TotalAmount FROM Tickets t WITH (UPDLOCK,HOLDLOCK)
WHERE t.TicketId=@TicketId AND t.TicketCode=@TicketCode AND t.Status=N'Đã đặt'
  AND t.PaymentStatus=N'Chưa thanh toán' AND t.PaymentExpiresAt>SYSUTCDATETIME();
IF @Amount IS NULL THROW 51010, 'Ticket is not payable.', 1;
IF EXISTS (SELECT 1 FROM Payments WHERE TicketId=@TicketId AND Status IN (N'Pending',N'Success'))
    THROW 51011, 'Payment already exists.', 1;";

    private static async Task<PaymentResult> CreateCashPaymentAsync(PaymentRequest request)
    {
        // Pay-on-board: the seat stays held until the coach leaves, not for 15 minutes.
        const string cashSql = @"
DECLARE @PaymentId int;
DECLARE @Amount decimal(18,2);
DECLARE @Departure datetime2;
SELECT @Amount=t.TotalAmount, @Departure=tr.DepartureTime
FROM Tickets t WITH (UPDLOCK,HOLDLOCK)
JOIN Trips tr ON tr.TripId=t.TripId
WHERE t.TicketId=@TicketId AND t.TicketCode=@TicketCode AND t.Status=N'Đã đặt'
  AND t.PaymentStatus=N'Chưa thanh toán' AND t.PaymentExpiresAt>SYSUTCDATETIME();
IF @Amount IS NULL THROW 51010, 'Ticket is not payable.', 1;
IF EXISTS (SELECT 1 FROM Payments WHERE TicketId=@TicketId AND Status IN (N'Pending',N'Success'))
    THROW 51011, 'Payment already exists.', 1;
IF DATEADD(MINUTE, 60, @Departure) <= SYSUTCDATETIME() THROW 51012, 'Trip already departed beyond the 60-minute grace period.', 1;
INSERT INTO Payments(TicketId,TransactionNo,Provider,ProviderTransactionNo,OrderInfo,PaymentMethod,Amount,Status)
VALUES(@TicketId,@TransactionNoInput,N'Cash',NULL,@OrderInfo,N'Cash',@Amount,N'Pending');
SET @PaymentId = CAST(SCOPE_IDENTITY() AS int);
UPDATE Tickets SET PaymentExpiresAt=DATEADD(MINUTE, 60, @Departure) WHERE TicketId=@TicketId;
SELECT @PaymentId, @Amount;";
        SqlTransaction? transaction = null;
        try
        {
            var transactionNo = $"CASH{DateTime.UtcNow:yyyyMMddHHmmssfff}{RandomNumberGenerator.GetInt32(1000, 9999)}";
            await using var connection = DatabaseHelper.GetConnection();
            await connection.OpenAsync();
            // Serializable, like the VNPay path: in autocommit the guard's
            // UPDLOCK/HOLDLOCK is released at statement end, so two concurrent
            // calls could both pass it and insert a Pending row (AUD-C-002).
            transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
            await using var command = new SqlCommand(cashSql, connection, transaction);
            command.Parameters.Add("@TicketId", SqlDbType.Int).Value = request.TicketId;
            command.Parameters.Add("@TicketCode", SqlDbType.NVarChar, 30).Value = request.TicketCode;
            command.Parameters.Add("@TransactionNoInput", SqlDbType.NVarChar, 100).Value = transactionNo;
            command.Parameters.Add("@OrderInfo", SqlDbType.NVarChar, 255).Value = $"Cash on board for ticket {request.TicketCode}";

            int paymentId;
            await using (var reader = await command.ExecuteReaderAsync())
            {
                if (!await reader.ReadAsync())
                {
                    await RollbackQuietlyAsync(transaction);
                    return new(false, ErrorMessage: T("Không thể ghi nhận thanh toán tiền mặt.", "The cash payment could not be registered."));
                }
                paymentId = reader.GetInt32(0);
            }

            await transaction.CommitAsync();
            return new(true, paymentId, transactionNo, null);
        }
        catch (SqlException ex) when (ex.Number == 51010)
        { await RollbackQuietlyAsync(transaction); return new(false, ErrorMessage: T("Thời hạn giữ chỗ đã hết hoặc vé không còn đủ điều kiện thanh toán.", "The seat hold has expired or the ticket is no longer payable.")); }
        catch (SqlException ex) when (ex.Number is 51011 or 2601 or 2627)
        { await RollbackQuietlyAsync(transaction); return new(false, ErrorMessage: T("Vé này đã có giao dịch thanh toán.", "A payment already exists for this ticket.")); }
        catch (SqlException ex) when (ex.Number == 51012)
        { await RollbackQuietlyAsync(transaction); return new(false, ErrorMessage: T("Chuyến xe đã khởi hành hơn 60 phút nên không thể chọn tiền mặt trên xe nữa.", "This trip departed more than 60 minutes ago, so cash on board is no longer possible.")); }
        catch (SqlException ex)
        {
            await RollbackQuietlyAsync(transaction);
            LoggerService.LogError($"Could not create a cash payment for ticket {request.TicketCode}.", ex);
            return new(false, ErrorMessage: T("Không thể ghi nhận thanh toán tiền mặt.", "The cash payment could not be registered."));
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    private static async Task<PaymentResult> CreateBankTransferPaymentAsync(PaymentRequest request)
    {
        const string bankTransferSql = PayableTicketGuard + @"
DECLARE @PaymentId int;
INSERT INTO Payments(TicketId,TransactionNo,Provider,ProviderTransactionNo,OrderInfo,PaymentMethod,Amount,Status)
VALUES(@TicketId,@TransactionNoInput,N'BankTransfer',NULL,@OrderInfo,N'BankTransfer',@Amount,N'Pending');
SET @PaymentId = CAST(SCOPE_IDENTITY() AS int);
SELECT @PaymentId, @Amount;";
        SqlTransaction? transaction = null;
        try
        {
            var transactionNo = $"BANK{DateTime.UtcNow:yyyyMMddHHmmssfff}{RandomNumberGenerator.GetInt32(1000, 9999)}";
            await using var connection = DatabaseHelper.GetConnection();
            await connection.OpenAsync();
            // See CreateCashPaymentAsync: the guard is only atomic with the insert
            // inside an explicit transaction (AUD-C-002).
            transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
            await using var command = new SqlCommand(bankTransferSql, connection, transaction);
            command.Parameters.Add("@TicketId", SqlDbType.Int).Value = request.TicketId;
            command.Parameters.Add("@TicketCode", SqlDbType.NVarChar, 30).Value = request.TicketCode;
            command.Parameters.Add("@TransactionNoInput", SqlDbType.NVarChar, 100).Value = transactionNo;
            command.Parameters.Add("@OrderInfo", SqlDbType.NVarChar, 255).Value = $"Bank transfer for ticket {request.TicketCode}";

            int paymentId;
            await using (var reader = await command.ExecuteReaderAsync())
            {
                if (!await reader.ReadAsync())
                {
                    await RollbackQuietlyAsync(transaction);
                    return new(false, ErrorMessage: T("Không thể ghi nhận chuyển khoản.", "The bank transfer could not be registered."));
                }
                paymentId = reader.GetInt32(0);
            }

            await transaction.CommitAsync();
            return new(true, paymentId, transactionNo, null);
        }
        catch (SqlException ex) when (ex.Number == 51010)
        { await RollbackQuietlyAsync(transaction); return new(false, ErrorMessage: T("Thời hạn giữ chỗ đã hết hoặc vé không còn đủ điều kiện thanh toán.", "The seat hold has expired or the ticket is no longer payable.")); }
        catch (SqlException ex) when (ex.Number is 51011 or 2601 or 2627)
        { await RollbackQuietlyAsync(transaction); return new(false, ErrorMessage: T("Vé này đã có giao dịch thanh toán.", "A payment already exists for this ticket.")); }
        catch (SqlException ex)
        {
            await RollbackQuietlyAsync(transaction);
            LoggerService.LogError($"Could not create a bank transfer payment for ticket {request.TicketCode}.", ex);
            return new(false, ErrorMessage: T("Không thể ghi nhận chuyển khoản.", "The bank transfer could not be registered."));
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    private static async Task<PaymentResult> CreateVnPayPaymentAsync(PaymentRequest request)
    {
        if (!IsVnPayConfigured)
            return new(false, ErrorMessage: T("Thông tin xác thực VNPay chưa được cấu hình trên máy này.", "VNPay credentials are not configured on this machine."));

        const string amountSql = @"
SELECT t.TotalAmount FROM Tickets t WITH (UPDLOCK,HOLDLOCK)
WHERE t.TicketId=@TicketId AND t.TicketCode=@TicketCode AND t.Status=N'Đã đặt'
  AND t.PaymentStatus=N'Chưa thanh toán' AND t.PaymentExpiresAt>SYSUTCDATETIME();";
        const string insertSql = PayableTicketGuard + @"
DECLARE @PaymentId int;
IF @Amount <> @ExpectedAmount THROW 51013, 'Ticket price changed.', 1;
INSERT INTO Payments(TicketId,TransactionNo,Provider,ProviderTransactionNo,OrderInfo,PaymentMethod,Amount,Status)
VALUES(@TicketId,@TransactionNoInput,N'VNPay',NULL,@OrderInfo,N'VNPay',@Amount,N'Pending');
SET @PaymentId = CAST(SCOPE_IDENTITY() AS int);
SELECT @PaymentId, @Amount;";

        var transactionNo = $"VNP{DateTime.UtcNow:yyyyMMddHHmmssfff}{RandomNumberGenerator.GetInt32(1000, 9999)}";
        try
        {
            await using var connection = DatabaseHelper.GetConnection();
            await connection.OpenAsync();
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                await using var amountCommand = new SqlCommand(amountSql, connection, transaction);
                amountCommand.Parameters.Add("@TicketId", SqlDbType.Int).Value = request.TicketId;
                amountCommand.Parameters.Add("@TicketCode", SqlDbType.NVarChar, 30).Value = request.TicketCode;
                if (await amountCommand.ExecuteScalarAsync() is not decimal amount)
                {
                    await transaction.RollbackAsync();
                    return new(false, ErrorMessage: T("Thời hạn giữ chỗ đã hết hoặc vé không còn đủ điều kiện thanh toán.", "The seat hold has expired or the ticket is no longer payable."));
                }

                // The gateway URL is built before the ledger row exists so a configuration
                // failure can never leave an orphaned Pending payment behind.
                var paymentUrl = BuildVnPayUrl(transactionNo, request.TicketCode, amount);

                await using var insertCommand = new SqlCommand(insertSql, connection, transaction);
                insertCommand.Parameters.Add("@TicketId", SqlDbType.Int).Value = request.TicketId;
                insertCommand.Parameters.Add("@TicketCode", SqlDbType.NVarChar, 30).Value = request.TicketCode;
                insertCommand.Parameters.Add("@TransactionNoInput", SqlDbType.NVarChar, 100).Value = transactionNo;
                insertCommand.Parameters.Add("@OrderInfo", SqlDbType.NVarChar, 255).Value = $"VNPay payment for ticket {request.TicketCode}";
                var expected = insertCommand.Parameters.Add("@ExpectedAmount", SqlDbType.Decimal);
                expected.Precision = 18; expected.Scale = 2; expected.Value = amount;
                await using var reader = await insertCommand.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    await reader.CloseAsync();
                    await transaction.RollbackAsync();
                    return new(false, ErrorMessage: T("Không thể ghi nhận giao dịch VNPay.", "The VNPay transaction could not be registered."));
                }
                var paymentId = reader.GetInt32(0);
                await reader.CloseAsync();
                await transaction.CommitAsync();
                return new(true, paymentId, transactionNo, paymentUrl);
            }
            catch (InvalidOperationException ex)
            {
                await transaction.RollbackAsync();
                LoggerService.LogError($"VNPay could not be initialised for ticket {request.TicketCode}; no payment row was created.", ex);
                return new(false, ErrorMessage: T("Không thể chuẩn bị cổng VNPay cho đặt chỗ này.", "The VNPay gateway could not be prepared for this booking."));
            }
            catch (SqlException ex) when (ex.Number is 51010 or 51011 or 51013)
            {
                await transaction.RollbackAsync();
                return new(false, ErrorMessage: ex.Number == 51011
                    ? T("Vé này đã có giao dịch thanh toán.", "A payment already exists for this ticket.")
                    : T("Thời hạn giữ chỗ đã hết hoặc vé không còn đủ điều kiện thanh toán.", "The seat hold has expired or the ticket is no longer payable."));
            }
            catch (SqlException ex)
            {
                await transaction.RollbackAsync();
                LoggerService.LogError($"Could not create a VNPay payment for ticket {request.TicketCode}.", ex);
                return new(false, ErrorMessage: T("Không thể ghi nhận giao dịch VNPay.", "The VNPay transaction could not be registered."));
            }
        }
        catch (SqlException ex)
        {
            LoggerService.LogError($"Could not open a connection to create a VNPay payment for ticket {request.TicketCode}.", ex);
            return new(false, ErrorMessage: T("Không thể ghi nhận giao dịch VNPay.", "The VNPay transaction could not be registered."));
        }
    }

    public static async Task<PaymentVerificationResult> VerifyVnPayCallbackAsync(IReadOnlyDictionary<string, string> callback)
    {
        if (!IsValidSignature(callback))
            return new(false, false, T("Chữ ký phản hồi VNPay không hợp lệ.", "The VNPay callback signature is invalid."));
        if (!callback.TryGetValue("vnp_TmnCode", out var tmnCode) ||
            !string.Equals(tmnCode, AppConfig.VnPayTmnCode, StringComparison.Ordinal))
        {
            LoggerService.LogError("VNPay callback rejected: merchant code does not match the configured TMN code.");
            return new(false, false, T("Phản hồi VNPay được gửi cho một đơn vị thanh toán khác.", "The VNPay callback was issued for a different merchant."));
        }
        if (!callback.TryGetValue("vnp_TxnRef", out var transactionNo) || string.IsNullOrWhiteSpace(transactionNo) ||
            !callback.TryGetValue("vnp_Amount", out var amountText) ||
            !decimal.TryParse(amountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amountUnits) ||
            !callback.TryGetValue("vnp_ResponseCode", out var responseCode))
            return new(false, false, T("Phản hồi VNPay thiếu thông tin bắt buộc.", "The VNPay callback is missing required fields."));

        // VNPay signals a captured payment with BOTH vnp_ResponseCode and
        // vnp_TransactionStatus equal to "00". Treating the response code alone as
        // success settles tickets for transactions the gateway did not complete
        // (AUD-D-004). A missing field is not a success.
        var transactionStatus = callback.GetValueOrDefault("vnp_TransactionStatus");
        var gatewayCaptured = string.Equals(responseCode, "00", StringComparison.Ordinal)
                              && string.Equals(transactionStatus, "00", StringComparison.Ordinal);

        var amount = amountUnits / 100m;
        var providerTransactionNo = callback.GetValueOrDefault("vnp_TransactionNo");
        var raw = string.Join("&", callback.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}={x.Value}"));
        const string sql = @"
UPDATE p SET p.ResponseCode=@ResponseCode,p.RawResponse=@RawResponse,p.ProviderTransactionNo=@ProviderTransactionNo,
 p.VerifiedAt=SYSUTCDATETIME()
FROM Payments p WITH (UPDLOCK,HOLDLOCK)
WHERE p.TransactionNo=@TransactionNo AND p.Amount=@Amount AND p.Status=N'Pending'
 AND EXISTS(SELECT 1 FROM Tickets t WHERE t.TicketId=p.TicketId AND t.Status=N'Đã đặt' AND t.PaymentStatus=N'Chưa thanh toán' AND t.PaymentExpiresAt>SYSUTCDATETIME());
IF @@ROWCOUNT=0
BEGIN
 IF EXISTS(SELECT 1 FROM Payments WHERE TransactionNo=@TransactionNo AND Status=N'Success') SELECT 2;
 ELSE IF @Captured=1 AND EXISTS(SELECT 1 FROM Payments WHERE TransactionNo=@TransactionNo AND Amount=@Amount AND Status IN(N'Pending',N'Failed'))
 BEGIN
  /* The gateway captured the money but the seat hold had already lapsed, so the
     ticket can no longer be fulfilled. Record the capture and queue the payment
     for an operator refund instead of silently leaving it Pending until the
     expiry sweep marks it Failed with the customer charged (AUD-C-001). */
  UPDATE Payments
  SET Status=N'Success',PaidAt=SYSUTCDATETIME(),VerifiedAt=SYSUTCDATETIME(),
      ResponseCode=@ResponseCode,RawResponse=@RawResponse,ProviderTransactionNo=@ProviderTransactionNo,
      RefundRequestedAt=COALESCE(RefundRequestedAt,SYSUTCDATETIME()),
      Notes=COALESCE(Notes,N'Captured by VNPay after the seat hold expired. Refund required.')
  WHERE TransactionNo=@TransactionNo;
  SELECT 3;
 END
 ELSE SELECT 0;
END
ELSE IF @Captured=1
BEGIN
 UPDATE Payments SET Status=N'Success',PaidAt=SYSUTCDATETIME() WHERE TransactionNo=@TransactionNo;
 UPDATE Tickets SET PaymentStatus=N'Đã thanh toán',PaymentExpiresAt=NULL,Status=N'Đã thanh toán'
 WHERE TicketId=(SELECT TicketId FROM Payments WHERE TransactionNo=@TransactionNo) AND Status=N'Đã đặt' AND PaymentStatus=N'Chưa thanh toán';
 SELECT 1;
END
ELSE
BEGIN
 UPDATE Payments SET Status=N'Failed' WHERE TransactionNo=@TransactionNo;
 /* Release the hold the declined payment was made against, otherwise the seat
    stays blocked until the next sweep while the customer is told it was freed,
    and a retry is refused by the payable guard (AUD-C-004). */
 UPDATE Tickets SET Status=N'Đã hủy',CancelledAt=SYSUTCDATETIME(),PaymentExpiresAt=NULL
 WHERE TicketId=(SELECT TicketId FROM Payments WHERE TransactionNo=@TransactionNo)
   AND Status=N'Đã đặt' AND PaymentStatus=N'Chưa thanh toán';
 SELECT 0;
END";

        SqlTransaction? transaction = null;
        try
        {
            await using var connection = DatabaseHelper.GetConnection();
            await connection.OpenAsync();
            transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
            await using var command = new SqlCommand(sql, connection, transaction);
            command.Parameters.Add("@TransactionNo", SqlDbType.NVarChar, 100).Value = transactionNo;
            var amountParam = command.Parameters.Add("@Amount", SqlDbType.Decimal);
            amountParam.Precision = 18; amountParam.Scale = 2; amountParam.Value = amount;
            command.Parameters.Add("@ResponseCode", SqlDbType.NVarChar, 20).Value = responseCode;
            command.Parameters.Add("@ProviderTransactionNo", SqlDbType.NVarChar, 100).Value = (object?)providerTransactionNo ?? DBNull.Value;
            command.Parameters.Add("@RawResponse", SqlDbType.NVarChar, -1).Value = raw;
            command.Parameters.Add("@Captured", SqlDbType.Bit).Value = gatewayCaptured;
            var result = Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            await transaction.CommitAsync();
            LoggerService.LogInfo(
                $"VNPay callback for {transactionNo} processed with response code {responseCode}, " +
                $"transaction status {transactionStatus ?? "(absent)"} (outcome {result}).");
            if (result == 3)
            {
                LoggerService.LogWarning(
                    $"VNPay captured {amount} for {transactionNo} after the seat hold expired. " +
                    "The payment is queued for an operator refund.");
            }
            return result switch
            {
                1 => new(true, false),
                2 => new(true, true),
                3 => new(false, false,
                    T("VNPay đã thu tiền nhưng thời hạn giữ chỗ đã hết. Giao dịch được đưa vào danh sách chờ hoàn tiền; hãy liên hệ hỗ trợ kèm mã giao dịch.", "VNPay took the payment but the seat hold had already expired. The payment has been queued for a refund; contact support with the transaction reference.")),
                _ => new(false, false, T("VNPay từ chối thanh toán hoặc thời hạn giữ chỗ đã hết.", "The payment was declined by VNPay or the seat hold had already expired."))
            };
        }
        catch (SqlException ex)
        {
            await RollbackQuietlyAsync(transaction);
            LoggerService.LogError($"Database failure while verifying the VNPay callback for {transactionNo}.", ex);
            return new(false, false, T("Không thể xác minh thanh toán vì cơ sở dữ liệu không khả dụng.", "The payment could not be verified because the database is unavailable."));
        }
        catch (InvalidOperationException ex)
        {
            await RollbackQuietlyAsync(transaction);
            LoggerService.LogError($"Invalid state while verifying the VNPay callback for {transactionNo}.", ex);
            return new(false, false, T("Không thể xác minh thanh toán.", "The payment could not be verified."));
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    private static async Task RollbackQuietlyAsync(SqlTransaction? transaction)
    {
        if (transaction?.Connection is null) return;
        try { await transaction.RollbackAsync(); }
        catch (InvalidOperationException ex) { LoggerService.LogWarning($"Transaction rollback skipped: {ex.Message}"); }
        catch (SqlException ex) { LoggerService.LogError("Transaction rollback failed.", ex); }
    }

    public static async Task<string?> GetPaymentStatusAsync(string transactionNo)
    {
        if (string.IsNullOrWhiteSpace(transactionNo)) return null;
        const string sql = "SELECT Status FROM Payments WHERE TransactionNo=@TransactionNo;";
        await using var connection = DatabaseHelper.GetConnection();
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@TransactionNo", SqlDbType.NVarChar, 100).Value = transactionNo;
        return await command.ExecuteScalarAsync() as string;
    }

    /// <summary>
    /// Fails every payment whose seat hold has lapsed and releases the matching tickets.
    /// </summary>
    /// <returns>The number of seat holds released.</returns>
    /// <remarks>
    /// The two updates are one transaction: run separately, a connection drop between
    /// them left payments marked Failed while their tickets still held seats, and the
    /// combined row count told the caller nothing usable (AUD-C-010).
    /// </remarks>
    public static async Task<int> ExpirePendingPaymentsAsync()
    {
        const string sql = @"
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;
UPDATE Payments SET Status=N'Failed',ResponseCode=N'EXPIRED',VerifiedAt=SYSUTCDATETIME()
WHERE Status=N'Pending' AND TicketId IN (SELECT TicketId FROM Tickets WHERE PaymentExpiresAt<=SYSUTCDATETIME());
UPDATE Tickets SET Status=N'Đã hủy',PaymentStatus=N'Chưa thanh toán',CancelledAt=SYSUTCDATETIME()
WHERE Status=N'Đã đặt' AND PaymentStatus=N'Chưa thanh toán' AND PaymentExpiresAt<=SYSUTCDATETIME();
DECLARE @Released int = @@ROWCOUNT;
COMMIT TRANSACTION;
SELECT @Released;";
        await using var connection = DatabaseHelper.GetConnection();
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    // ---------------------------------------------------------------------
    // Settlement state machine — the only writers of a settled payment state.
    // ---------------------------------------------------------------------

    private const int OutcomeOk = 0;
    private const int OutcomeNotFound = 1;
    private const int OutcomeWrongPaymentState = 2;
    private const int OutcomeWrongMethod = 3;
    private const int OutcomeTicketNotEligible = 4;
    private const int OutcomeNoRefundRequest = 5;
    private const int OutcomeTicketAlreadyUsed = 7;

    /// <summary>
    /// Mirrors <c>AdminDatabaseService.RequireAdmin()</c>: the acting user must be the signed-in
    /// administrator and must be the same account the caller claims to be acting as.
    /// </summary>
    internal static string? ValidateAdminSession(int adminAccountId)
    {
        if (!CurrentUser.IsAdmin) return T("Cần quyền quản trị viên.", "Admin access required.");
        if (adminAccountId <= 0 || CurrentUser.Account is null || CurrentUser.Account.AccountId != adminAccountId)
            return T("Tài khoản nhân viên không khớp với phiên hiện tại.", "The operator account does not match the current session.");
        return null;
    }

    public static async Task<(bool Success, string? Error)> ConfirmManualPaymentAsync(int paymentId, int adminAccountId, string? providerReference, string? note)
    {
        if (ValidateAdminSession(adminAccountId) is { } authError) return (false, authError);
        if (CurrentUser.IsOwner)
            return (false, T("Chủ sở hữu (Owner) không có quyền duyệt thanh toán vé; thao tác đối soát thu tiền dành cho Quản trị viên / Nhân viên bến.", "The Owner cannot approve ticket settlements; payment confirmation is reserved for Administrators / Station Staff."));
        if (paymentId <= 0) return (false, T("Chưa chọn giao dịch thanh toán.", "No payment was selected."));

        const string sql = @"
DECLARE @Result int = 0;
DECLARE @TicketId int, @PaymentState nvarchar(20), @Method nvarchar(50), @TicketState nvarchar(50);
SELECT @TicketId=p.TicketId, @PaymentState=p.Status, @Method=p.PaymentMethod
FROM Payments p WITH (UPDLOCK,HOLDLOCK) WHERE p.PaymentId=@PaymentId;
IF @TicketId IS NULL SET @Result=1;
ELSE IF @PaymentState<>N'Pending' SET @Result=2;
ELSE IF @Method NOT IN (N'BankTransfer',N'Cash') SET @Result=3;
ELSE
BEGIN
    SELECT @TicketState=t.Status FROM Tickets t WITH (UPDLOCK,HOLDLOCK) WHERE t.TicketId=@TicketId;
    IF @TicketState IS NULL OR @TicketState<>N'Đã đặt' SET @Result=4;
    ELSE
    BEGIN
        UPDATE Payments SET Status=N'Success', PaidAt=SYSUTCDATETIME(), VerifiedAt=SYSUTCDATETIME(),
               ProviderTransactionNo=@ProviderReference, ConfirmedByAccountId=@AdminAccountId, Notes=@Note
        WHERE PaymentId=@PaymentId;
        UPDATE Tickets SET Status=N'Đã thanh toán', PaymentStatus=N'Đã thanh toán', PaymentExpiresAt=NULL
        WHERE TicketId=@TicketId;
    END
END
SELECT @Result;";

        return await ExecuteSettlementAsync(sql, $"confirm payment {paymentId}", command =>
        {
            command.Parameters.Add("@PaymentId", SqlDbType.Int).Value = paymentId;
            command.Parameters.Add("@AdminAccountId", SqlDbType.Int).Value = adminAccountId;
            command.Parameters.Add("@ProviderReference", SqlDbType.NVarChar, 100).Value = TextOrNull(providerReference);
            command.Parameters.Add("@Note", SqlDbType.NVarChar, 500).Value = TextOrNull(note);
        }, outcome => outcome switch
        {
            OutcomeNotFound => T("Giao dịch thanh toán không còn tồn tại.", "That payment no longer exists."),
            OutcomeWrongPaymentState => T("Giao dịch này đã được đối soát.", "This payment has already been settled."),
            OutcomeWrongMethod => T("Chỉ có thể xác nhận thủ công giao dịch chuyển khoản và tiền mặt.", "Only bank transfers and cash payments can be confirmed manually."),
            OutcomeTicketNotEligible => T("Vé không còn đủ điều kiện: đã hủy, hết hạn hoặc đã thanh toán.", "The ticket is no longer eligible: it was cancelled, expired or already paid."),
            _ => null
        }, "PaymentConfirmed", paymentId.ToString(CultureInfo.InvariantCulture));
    }

    public static async Task<(bool Success, string? Error)> RejectManualPaymentAsync(int paymentId, int adminAccountId, string? reason)
    {
        if (ValidateAdminSession(adminAccountId) is { } authError) return (false, authError);
        if (paymentId <= 0) return (false, T("Chưa chọn giao dịch thanh toán.", "No payment was selected."));

        const string sql = @"
DECLARE @Result int = 0;
DECLARE @TicketId int, @PaymentState nvarchar(20);
SELECT @TicketId=p.TicketId, @PaymentState=p.Status
FROM Payments p WITH (UPDLOCK,HOLDLOCK) WHERE p.PaymentId=@PaymentId;
IF @TicketId IS NULL SET @Result=1;
ELSE IF @PaymentState<>N'Pending' SET @Result=2;
ELSE
BEGIN
    UPDATE Payments SET Status=N'Failed', VerifiedAt=SYSUTCDATETIME(), ConfirmedByAccountId=@AdminAccountId, Notes=@Reason
    WHERE PaymentId=@PaymentId;
    UPDATE Tickets SET Status=N'Đã hủy', CancelledAt=SYSUTCDATETIME(), PaymentExpiresAt=NULL
    WHERE TicketId=@TicketId AND Status=N'Đã đặt';
END
SELECT @Result;";

        return await ExecuteSettlementAsync(sql, $"reject payment {paymentId}", command =>
        {
            command.Parameters.Add("@PaymentId", SqlDbType.Int).Value = paymentId;
            command.Parameters.Add("@AdminAccountId", SqlDbType.Int).Value = adminAccountId;
            command.Parameters.Add("@Reason", SqlDbType.NVarChar, 500).Value = TextOrNull(reason);
        }, outcome => outcome switch
        {
            OutcomeNotFound => T("Giao dịch thanh toán không còn tồn tại.", "That payment no longer exists."),
            OutcomeWrongPaymentState => T("Chỉ có thể từ chối giao dịch đang chờ.", "Only a pending payment can be rejected."),
            _ => null
        }, "PaymentRejected", paymentId.ToString(CultureInfo.InvariantCulture));
    }

    public static async Task<(bool Success, string? Error)> ApproveRefundAsync(int paymentId, int adminAccountId, string? note)
    {
        if (ValidateAdminSession(adminAccountId) is { } authError) return (false, authError);
        if (paymentId <= 0) return (false, T("Chưa chọn giao dịch thanh toán.", "No payment was selected."));

        const string sql = @"
DECLARE @Result int = 0;
DECLARE @TicketId int, @PaymentState nvarchar(20), @Requested datetime2, @TicketState nvarchar(50);
SELECT @TicketId=p.TicketId, @PaymentState=p.Status, @Requested=p.RefundRequestedAt
FROM Payments p WITH (UPDLOCK,HOLDLOCK) WHERE p.PaymentId=@PaymentId;
IF @TicketId IS NULL SET @Result=1;
ELSE IF @PaymentState<>N'Success' SET @Result=2;
ELSE IF @Requested IS NULL SET @Result=5;
ELSE
BEGIN
    -- take the ticket row lock before mutating either side of the pair
    SELECT @TicketState=t.Status FROM Tickets t WITH (UPDLOCK,HOLDLOCK) WHERE t.TicketId=@TicketId;
    /* A travelled ticket must not be refunded: the state was read but never
       checked, so a used ticket could be paid back (AUD-C-006). */
    IF @TicketState=N'Đã sử dụng' SET @Result=7;
    ELSE
    BEGIN
        UPDATE Payments SET Status=N'Refunded', RefundedAt=SYSUTCDATETIME(),
               RefundApprovedByAccountId=@AdminAccountId, Notes=@Note
        WHERE PaymentId=@PaymentId;
        UPDATE Tickets SET PaymentStatus=N'Hoàn tiền' WHERE TicketId=@TicketId;
    END
END
SELECT @Result;";

        return await ExecuteSettlementAsync(sql, $"approve refund for payment {paymentId}", command =>
        {
            command.Parameters.Add("@PaymentId", SqlDbType.Int).Value = paymentId;
            command.Parameters.Add("@AdminAccountId", SqlDbType.Int).Value = adminAccountId;
            command.Parameters.Add("@Note", SqlDbType.NVarChar, 500).Value = TextOrNull(note);
        }, outcome => outcome switch
        {
            OutcomeNotFound => T("Giao dịch thanh toán không còn tồn tại.", "That payment no longer exists."),
            OutcomeWrongPaymentState => T("Chỉ có thể hoàn tiền giao dịch đã đối soát.", "Only a settled payment can be refunded."),
            OutcomeNoRefundRequest => T("Hành khách chưa yêu cầu hoàn tiền cho giao dịch này.", "The passenger has not requested a refund for this payment."),
            OutcomeTicketAlreadyUsed => T("Vé này đã được sử dụng để đi xe nên không thể hoàn tiền.", "This ticket has already been used for travel, so the payment cannot be refunded."),
            _ => null
        }, "RefundApproved", paymentId.ToString(CultureInfo.InvariantCulture));
    }

    private static object TextOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

    private static async Task<(bool Success, string? Error)> ExecuteSettlementAsync(
        string sql, string operation, Action<SqlCommand> bind, Func<int, string?> describe,
        string? auditAction = null, string? auditTargetId = null)
    {
        SqlTransaction? transaction = null;
        try
        {
            await using var connection = DatabaseHelper.GetConnection();
            await connection.OpenAsync();
            transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
            await using var command = new SqlCommand(sql, connection, transaction);
            bind(command);
            var outcome = Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            if (outcome != OutcomeOk)
            {
                await transaction.RollbackAsync();
                return (false, describe(outcome) ?? T("Sổ thanh toán từ chối thao tác này.", "The operation was refused by the payment ledger."));
            }
            // The audit row commits with the state change it describes (AUD-D-009).
            if (auditAction is not null)
                await AuditLog.WriteAsync(connection, transaction, auditAction, "Payment", auditTargetId);
            await transaction.CommitAsync();
            LoggerService.LogInfo($"Settlement succeeded: {operation}.");
            return (true, null);
        }
        catch (SqlException ex)
        {
            await RollbackQuietlyAsync(transaction);
            LoggerService.LogError($"Database failure while trying to {operation}.", ex);
            return (false, T("Sổ thanh toán không khả dụng. Vui lòng thử lại.", "The payment ledger is unavailable. Please try again."));
        }
        catch (InvalidOperationException ex)
        {
            await RollbackQuietlyAsync(transaction);
            LoggerService.LogError($"Invalid state while trying to {operation}.", ex);
            return (false, T("Không thể hoàn tất thao tác.", "The operation could not be completed."));
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    // ---------------------------------------------------------------------
    // VNPay signing
    // ---------------------------------------------------------------------

    public static string BuildVnPayUrl(string transactionNo, string orderInfo, decimal amount)
    {
        var tmnCode = AppConfig.VnPayTmnCode;
        var secret = AppConfig.VnPayHashSecret;
        if (string.IsNullOrWhiteSpace(tmnCode) || string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("VNPay configuration is missing.");
        var localNow = DateTime.UtcNow.AddHours(7);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["vnp_Version"] = "2.1.0",
            ["vnp_Command"] = "pay",
            ["vnp_TmnCode"] = tmnCode,
            ["vnp_Amount"] = ((long)(amount * 100)).ToString(CultureInfo.InvariantCulture),
            ["vnp_CurrCode"] = "VND",
            ["vnp_TxnRef"] = transactionNo,
            ["vnp_OrderInfo"] = orderInfo,
            ["vnp_OrderType"] = "other",
            ["vnp_Locale"] = "vn",
            ["vnp_ReturnUrl"] = AppConfig.VnPayReturnUrl,
            ["vnp_IpAddr"] = "127.0.0.1",
            ["vnp_CreateDate"] = localNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture),
            ["vnp_ExpireDate"] = localNow.AddMinutes(15).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)
        };
        var query = BuildCanonicalQuery(fields);
        var signature = BuildSignature(fields, secret);
        return $"{VnPayEndpoint}?{query}&vnp_SecureHash={signature}";
    }

    /// <summary>Ordinal-sorted, <see cref="WebUtility.UrlEncode"/>d <c>key=value</c> pairs — the exact payload VNPay signs.</summary>
    private static string BuildCanonicalQuery(IReadOnlyDictionary<string, string> fields) =>
        string.Join("&", fields
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => $"{WebUtility.UrlEncode(x.Key)}={WebUtility.UrlEncode(x.Value)}"));

    internal static string BuildSignature(IReadOnlyDictionary<string, string> fields, string hashSecret)
    {
        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(hashSecret));
        var payload = Encoding.UTF8.GetBytes(BuildCanonicalQuery(fields));
        return Convert.ToHexString(hmac.ComputeHash(payload)).ToLowerInvariant();
    }

    internal static bool IsValidSignature(IReadOnlyDictionary<string, string> callback)
    {
        var secret = AppConfig.VnPayHashSecret;
        if (string.IsNullOrWhiteSpace(secret))
        {
            LoggerService.LogError("VNPay callback rejected: the hash secret is not configured, so no signature can be trusted.");
            return false;
        }
        if (!callback.TryGetValue("vnp_SecureHash", out var expected) || string.IsNullOrWhiteSpace(expected))
        {
            LoggerService.LogError("VNPay callback rejected: vnp_SecureHash is missing.");
            return false;
        }
        var signed = callback
            .Where(x => x.Key is not "vnp_SecureHash" and not "vnp_SecureHashType")
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        var actual = BuildSignature(signed, secret);
        var matches = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(actual),
            Encoding.UTF8.GetBytes(expected.Trim().ToLowerInvariant()));
        if (!matches) LoggerService.LogError("VNPay callback rejected: signature mismatch.");
        return matches;
    }
}
