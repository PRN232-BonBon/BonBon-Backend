# Booking approval (Giai đoạn 2B)

## API

- `POST /api/bookings/{bookingId}/approve`
- `POST /api/bookings/{bookingId}/reject`

JWT phải có role `VehicleOwner`; AccountId lấy từ claim, không lấy từ body.
Hai endpoint không có request body. Các trường client gửi như giá, chủ xe,
trạng thái không được bind hoặc sử dụng. Role trong JWT chỉ là lớp đầu tiên:
repository kiểm tra lại quyền sở hữu, Account và AccountRole trong database.
Role vừa được cấp cần refresh token để nhận claim mới.

Thành công: HTTP 200 với `bookingId`, `status`, `unitPrice`,
`quotedRentalAmount`, `currency`, `isReplay`.

| Trường hợp | HTTP / mã lỗi |
| --- | --- |
| Chưa đăng nhập / claim AccountId không hợp lệ | 401 |
| Thiếu role VehicleOwner trong JWT | 403 |
| BookingId rỗng (Guid.Empty) | 400 / BOOKING_ID_INVALID |
| Booking không tồn tại | 404 / BOOKING_NOT_FOUND |
| Không sở hữu xe | 403 / BOOKING_NOT_OWNER |
| Account/role chủ xe không Active | 403 / OWNER_INACTIVE |
| Account/role khách không Active khi duyệt | 409 / CUSTOMER_INACTIVE |
| Xe không Available khi duyệt | 409 / VEHICLE_UNAVAILABLE |
| Qua thời gian nhận xe khi duyệt | 409 / BOOKING_WINDOW_PASSED |
| Trùng Booking giữ lịch hoặc availability_block | 409 / SCHEDULE_CONFLICT |
| Quyết định ngược hoặc trạng thái đã tiến xa hơn | 409 / BOOKING_STATE_CONFLICT |
| Deadlock, serialization, timeout khóa hoặc dữ liệu vừa đổi | 409 / BOOKING_DECISION_RETRY |
| Database từ chối bằng check constraint/trigger | 409 / BOOKING_DECISION_REJECTED |

Chỉ ghi hai chuyển trạng thái: PendingApproval -> AwaitingPayment/Rejected.
Gửi lại approve khi đang AwaitingPayment hoặc reject khi đang Rejected trả 200,
isReplay=true, không ghi lại. Nếu đã Confirmed hoặc tiến xa hơn, không suy đoán
lịch sử quyết định mà trả 409. Chủ xe vẫn phải có Account/role Active khi replay.
Từ chối cho phép khách không hoạt động hoặc xe không Available để dọn yêu cầu chờ.

## Transaction và lịch xe

Read Committed, lock_timeout=5s, statement_timeout=15s cho riêng transaction.
1. Đọc vehicle_id của Booking, chưa khóa Booking.
2. Khóa Vehicle FOR UPDATE.
3. Khóa Booking FOR UPDATE, đọc lại toàn bộ dữ liệu cần quyết định.
4. Nếu vehicle_id vừa đổi (schema cũ), rollback và trả mã retry; không khóa xe thứ hai.
5. Xác minh chủ xe; khóa SHARE Account/AccountRole của các bên theo thứ tự ổn định,
   chặn thay trạng thái account/role trong lúc quyết định.
6. Kiểm tra trạng thái/idempotency; khi duyệt kiểm tra khách, xe, thời gian và lịch.
7. Chỉ UPDATE status với điều kiện status='PendingApproval'; commit.

Mọi nhánh thất bại rollback khi dispose. Giá snapshot, tổng tiền generated và
thời gian thuê giữ nguyên. [start,end) cho phép hai lượt thuê liền kề. Các trạng
thái giữ lịch khớp repository hiện có và exclusion constraint trong schema 001.
23P01 được chuyển thành lỗi lịch; deadlock/lock timeout trả retry thay vì SQL nội bộ.
Không tự retry request ghi khi chưa có yêu cầu mới từ client.

Các API thay đổi lịch/availability mới phải tiếp tục khóa Vehicle trước Booking.
Admin sửa Account rồi Vehicle theo thứ tự khác có thể gây deadlock; API hiện trả
retry có kiểm soát. Không gọi API này trên database dùng chung để thử nghiệm.

## Kiểm thử an toàn

`dotnet test BonBon.sln --configuration Release`

Unit/API tests dùng mock và TestServer riêng, không khởi chạy Program,
không đọc appsettings và không đăng ký DbContext. API tests kiểm tra middleware
authorization bằng authentication handler giả; không thay thế kiểm thử JWT thực tế.

Integration tests mặc định SKIP. Chỉ bật trên PostgreSQL 16 LOCAL dành riêng cho test:

```powershell
# Chỉ dùng thông tin của một PostgreSQL local dùng một lần, không dùng SSH tunnel
# hoặc server/credential của nhóm. Không cấu hình các biến này trong production.
$env:BONBON_TEST_POSTGRES_ADMIN = 'Host=127.0.0.1;Port=55432;Database=postgres;Username=postgres;Password=<local-test-password>'
$env:BONBON_TEST_POSTGRES_CONSENT = 'I_CONFIRM_DISPOSABLE_LOCAL_POSTGRES'
dotnet test BonBon.sln --configuration Release --filter FullyQualifiedName~BookingDecisionPostgresTests
```

Test chỉ chấp nhận host loopback và admin database `postgres`; không fallback sang
DefaultConnection, .env hoặc appsettings. Mỗi case tạo database tên UUID riêng
`bonbon_booking_test_<guid>`, chạy 001 và seed dữ liệu trong database đó, sau đó
xóa đúng database vừa tạo. Tài khoản local cần quyền CREATE/DROP DATABASE và cài
btree_gist. Test đồng thời dùng hai connection/DbContext độc lập, giữ khóa xe làm
barrier và xác minh cả hai transaction đang chờ khóa trước khi giải phóng.
Nếu test process bị kill đột ngột, có thể còn database test cần dọn thủ công.

## Schema và đề xuất migration riêng (chưa thực thi)

Implementation hiện tại không cần cột mới và không chạy migration lúc startup.
Baseline yêu cầu: booking.billable_units, start_at/end_at, unit_price_snapshot,
quoted_rental_amount, FK và trạng thái theo schema hiện tại. Schema thực tế của
nhóm vẫn cần kiểm kê read-only trước khi triển khai ứng dụng.

001 hiện đã chứa schema thuê linh hoạt; 002 lại rename quantity, xóa hàm cũ và
thay protect_booking_quote bằng phiên bản yếu hơn. Không chạy tuần tự 001/002/003
trên database mới; 003 không khôi phục phần bảo vệ trigger mà 002 đã thay.
Integration hiện kiểm tra baseline 001, không khẳng định mọi schema cũ tương đương.

Đề xuất migration tương lai riêng sau khi nhóm xác nhận schema:
- `004_booking_decision_audit.sql`: thêm nullable `payment_due_at timestamptz`,
  `decided_at timestamptz`, `decided_by_account_id uuid` FK account; bảng
  `booking_decision` gồm booking_id, actor_account_id, from_status, to_status,
  decided_at, reason tùy chọn; chỉ ghi audit cùng transaction với quyết định.
- Thống nhất protect_booking_quote: không thay participants; không quay lại
  PendingApproval; không thay thời gian/giá sau quyết định. Kiểm tra dữ liệu cũ
  trước khi thêm constraint, không sửa lịch sử bằng backfill suy đoán.
- Chốt TTL, quy tắc hết hạn/tiền đến muộn trước khi ghi payment_due_at.
- Kiểm thử migration trên database tách biệt cả baseline mới và bản sao schema cũ.
  Triển khai schema trước code phụ thuộc cột mới; không chạy file trên database
  dùng chung nếu chưa có quy trình duyệt riêng của nhóm.

Audit, lý do từ chối lưu DB, thời hạn thanh toán và tác vụ giải phóng lịch chưa
được triển khai ở 2B. Hiện AwaitingPayment giữ lịch đến khi có workflow xử lý tiếp.
Chưa có UI chủ xe, VNPay, Payment API hoặc Wallet trong thay đổi này.
