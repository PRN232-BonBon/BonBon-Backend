# Payment working tree — BonBon-Backend

Ngày: 23/10/2026. Branch: `feature/payment`. Không commit/push/stage.
Snapshot gồm cả thay đổi từ các giai đoạn trước; M=tracked modified, ??=untracked.
File ignored chứa secret không có trong danh mục này.

## File sửa/tạo riêng trong review cuối

- `.dockerignore`
- `Dockerfile`
- `BonBon.API/BonBon.API.csproj`
- `BonBon.API/Program.cs`
- `BonBon.API/appsettings.Development.json`
- `BonBon.Business/Services/Implementations/PaymentNotificationPolicy.cs`
- `BonBon.Business/Services/Implementations/VnPayGateway.cs`
- `BonBon.Tests/Payments/PaymentNotificationPolicyTests.cs`
- `BonBon.Tests/Payments/VnPayGatewayTests.cs`
- `docs/PAYMENT_HANDOFF.md`
- `docs/PAYMENT_WORKTREE_MANIFEST.md`


## Toàn bộ thay đổi chưa commit

```text
 M .dockerignore
 M .env.example
 M .gitignore
 M BonBon.API/BonBon.API.csproj
 M BonBon.API/Controllers/BookingsController.cs
 M BonBon.API/Program.cs
 M BonBon.API/appsettings.Development.json
 M BonBon.API/appsettings.json
 M BonBon.Business/DTOs/Bookings/BookingResponses.cs
 M BonBon.Business/Services/Implementations/BookingService.cs
 M BonBon.Business/Services/Interfaces/IBookingService.cs
 M BonBon.DataAccess/Repositories/Implementations/BookingRepository.cs
 M BonBon.DataAccess/Repositories/Interfaces/IBookingRepository.cs
 M BonBon.sln
 M Dockerfile
 M compose.yaml
?? BonBon.API/Configuration/PaymentStaging.cs
?? BonBon.API/Controllers/PaymentController.cs
?? BonBon.Business/Configuration/PaymentOptions.cs
?? BonBon.Business/Configuration/PaymentReturnOptions.cs
?? BonBon.Business/Configuration/VnPayOptions.cs
?? BonBon.Business/DTOs/Payments/PaymentRequests.cs
?? BonBon.Business/DTOs/Payments/PaymentResponses.cs
?? BonBon.Business/Services/Implementations/PaymentNotificationPolicy.cs
?? BonBon.Business/Services/Implementations/PaymentService.cs
?? BonBon.Business/Services/Implementations/VnPayGateway.cs
?? BonBon.Business/Services/Interfaces/IPaymentGateway.cs
?? BonBon.Business/Services/Interfaces/IPaymentService.cs
?? BonBon.DataAccess/Repositories/Implementations/BookingRepository.Decisions.cs
?? BonBon.DataAccess/Repositories/Implementations/PaymentRepository.cs
?? BonBon.DataAccess/Repositories/Interfaces/BookingDecisionResult.cs
?? BonBon.DataAccess/Repositories/Interfaces/IPaymentRepository.cs
?? BonBon.DataAccess/Repositories/Interfaces/PaymentContracts.cs
?? BonBon.Tests/BonBon.Tests.csproj
?? BonBon.Tests/Bookings/BookingDecisionApiTests.cs
?? BonBon.Tests/Bookings/BookingDecisionServiceTests.cs
?? BonBon.Tests/Integration/BookingDecisionPostgresTests.cs
?? BonBon.Tests/Integration/DisposablePostgresDatabase.cs
?? BonBon.Tests/Integration/PaymentPostgresFixture.cs
?? BonBon.Tests/Integration/PaymentPostgresTests.cs
?? BonBon.Tests/Integration/PaymentStagingReadinessTests.cs
?? BonBon.Tests/Integration/PaymentStagingSmokeTests.cs
?? BonBon.Tests/Integration/PostgresSafetyTests.cs
?? BonBon.Tests/Integration/SharedPostgresReadOnlyTests.cs
?? BonBon.Tests/Integration/SharedPostgresSettings.cs
?? BonBon.Tests/Payments/PaymentApiTests.cs
?? BonBon.Tests/Payments/PaymentNotificationPolicyTests.cs
?? BonBon.Tests/Payments/PaymentServiceTests.cs
?? BonBon.Tests/Payments/PaymentStagingMerchantLocalTests.cs
?? BonBon.Tests/Payments/PaymentStagingSafetyTests.cs
?? BonBon.Tests/Payments/PaymentTestData.cs
?? BonBon.Tests/Payments/VnPayGatewayTests.cs
?? database/proposals/004_vnpay_sandbox.sql
?? database/proposals/005_booking_quote_guard.sql
?? deployment/payment-staging.Caddyfile.example
?? docs/PAYMENT_HANDOFF.md
?? docs/PAYMENT_WORKTREE_MANIFEST.md
?? docs/booking-approval-shared-checks.md
?? docs/booking-approval.md
?? docs/payment-postgres-tests-2e2.md
?? docs/payment-schema-review-2e1.md
?? docs/payment-staging-2f1.md
?? docs/payment-staging-2f2.md
?? docs/payment-vnpay-sandbox.md
?? tools/Initialize-PaymentStaging.ps1
?? tools/Start-PaymentStaging.ps1
```

## Final security review trước public push

Không stage/commit/push. Không DB/VNPay/E2E/migration. Danh sách file vẫn khớp snapshot.

Source/config/test cập nhật:
.gitignore; BonBon.API/Configuration/PaymentStaging.cs; tools/Initialize-PaymentStaging.ps1;
BonBon.Tests/Integration/{SharedPostgresSettings,PaymentPostgresFixture,PostgresSafetyTests,SharedPostgresReadOnlyTests}.cs;
BonBon.Tests/Payments/PaymentStagingSafetyTests.cs.

Tài liệu được làm sạch metadata: docs/PAYMENT_HANDOFF.md, docs/booking-approval-shared-checks.md,
docs/payment-postgres-tests-2e2.md, docs/payment-staging-2f1.md, docs/payment-staging-2f2.md.
Manifest này đã cập nhật. File local/archive chứa metadata/secret vẫn ignored và không nằm trong danh sách.

Giữ nguyên database identity/marker/role/permissions, chỉ chuyển host pin bắt buộc sang local config.
Kết quả: build0 warning/error;191 passed,0 failed,42 skipped. Credential DB/JWT trong lịch sử cần quản trị viên xử lý.
