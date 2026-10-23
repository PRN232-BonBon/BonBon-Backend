# Giai đoạn 2B.1 — kiểm tra database nhóm (2026-10-22)

## Phạm vi đã được người dùng xác nhận

Ban đầu cho phép kiểm thử có kiểm soát, nhưng điều kiện backup chưa được xác nhận.
Người dùng đã trả lời: **"Chưa xác nhận — chỉ kiểm tra đọc"**.
Do đó không chạy bất kỳ test ghi dữ liệu, tạo database, thay schema hoặc dọn dữ liệu nào.
Không suy luận việc cho phép kiểm thử ban đầu là xác nhận backup.

## Phân loại test hiện có

| Test / helper | Hành vi | Có chạy trên shared? |
| --- | --- | --- |
| BookingDecisionServiceTests | Mock repository; không kết nối DB | Có |
| BookingDecisionApiTests | TestServer riêng, mock service và authentication; không khởi chạy Program | Có, không truy cập DB |
| PostgresSafetyTests | Parse/kiểm tra cấu hình trong bộ nhớ, không mở connection | Có |
| DisposablePostgresDatabase.CreateAsync | CREATE DATABASE, chạy schema 001, INSERT seed đã commit | Không |
| DisposablePostgresDatabase.DisposeAsync | DROP DATABASE WITH FORCE | Không |
| Approve_preserves_quote_and_replay_does_not_write_again | Seed, UPDATE vehicle không có WHERE, duyệt Booking | Không |
| Reject_is_idempotent_and_cannot_later_be_approved | Seed, từ chối Booking | Không |
| Missing_booking_and_other_owner_never_change_booking | Vẫn tạo DB/schema/seed dù assertion có nhánh không ghi | Không |
| Rechecks_current_eligibility | UPDATE Account/Role theo ID; nhánh vehicle UPDATE không WHERE | Không |
| Cannot_decide_a_booking_after_other_workflow_transitions | UPDATE Booking của fixture | Không |
| Availability_block_prevents_approval_but_allows_rejection | INSERT block, từ chối Booking | Không |
| Adjacent_intervals_can_both_be_approved | UPDATE lịch Booking, duyệt hai Booking | Không |
| Past_pickup_time_cannot_be_approved | UPDATE toàn bộ booking của database fixture | Không |
| Concurrent_decisions_serialize_on_vehicle | SELECT toàn bộ vehicle FOR UPDATE, hai connection ghi Booking | Không |
| Existing_quote_create_and_mine_still_work | Tạo Booking qua hàm database | Không |

Cả 10 nhóm PostgreSQL cũ đều phụ thuộc fixture tạo/xóa database. Không tái sử dụng
fixture, không bỏ guard và không trỏ BONBON_TEST_POSTGRES_ADMIN vào database nhóm.

## Hai kiểm tra shared chỉ đọc mới

- `Target_and_schema_are_inspected_without_writes`: đọc catalog về target,
  cột, constraint, trigger, hàm; không SELECT dữ liệu người dùng.
- `Missing_booking_returns_not_found_in_a_read_only_session`: gọi service thật
  và repository thật với UUID ngẫu nhiên không tồn tại; xác nhận approve/reject
  trả BOOKING_NOT_FOUND. Không đi vào đường khóa Vehicle/Booking thực tế.

Secret được đọc trong bộ nhớ từ biến BONBON_SHARED_POSTGRES_CONNECTION hoặc file
local được chỉ rõ bằng BONBON_SHARED_POSTGRES_CONFIG_FILE; không tự động fallback.
Lần này sử dụng appsettings.Development.json local đang được Git ignore.
Không in connection string, password, tham số người dùng hoặc exception detail.
Chỉ chấp nhận host Tailscale đã được xác nhận, database bonbon, username bonbon_app.

Mọi connection có default_transaction_read_only=on ở server, timeout kết nối 5s,
statement timeout 5s, lock timeout 1s, idle-in-transaction timeout 10s; pooling tắt.
Metadata transaction rollback khi hoàn tất. Không gọi create_booking(), không
chạy DDL, không dùng FOR UPDATE với ID có sẵn. Hàm được đối chiếu bằng source
body đã chuẩn hóa whitespace và SHA256, không thực thi hàm để kiểm tra.

## Kết quả thực tế

- Kết nối thành công qua <approved-db-host-in-local-config>:5432.
- current_database=bonbon; current_user=bonbon_app; PostgreSQL=16.15.
- transaction_read_only=on; pg_is_in_recovery=false.
- booking có billable_units, start_at/end_at và cột giá snapshot.
- quoted_rental_amount là GENERATED ALWAYS từ billable_units * unit_price_snapshot.
- ex_booking_vehicle_schedule đã validated, dùng GiST và khoảng [start,end),
  bao gồm AwaitingPayment cùng các trạng thái giữ lịch hiện tại.
- Trigger booking_schedule_guard và availability_block_schedule_guard đang bật.
- check_vehicle_schedule, check_actor_eligibility, has_active_role và
  protect_vehicle_owner khớp body trong 001 sau chuẩn hóa whitespace.
- create_booking có 5 tham số (customer, vehicle, rental_type, start_at, end_at),
  body khớp phiên bản linh hoạt trong 001/002.
- **protect_booking_quote khớp body 002, không khớp 001 hiện tại.** Không có kiểm
  tra bất biến customer_account_id hoặc chặn quay về PendingApproval như 001.
  Đây là schema cần review, không sửa tự động. Hai endpoint mới chỉ UPDATE status,
  nhưng kiểm tra read-only không chứng minh mọi đường ghi khác đều được bảo vệ.

Build Release: thành công. Bộ test: 59 tổng, 49 passed, 0 failed, 10 nhóm skipped.
Trong 49 passed: 40 service/API, 7 safety trong bộ nhớ, **2 integration shared chỉ đọc**.
Số bản ghi ứng dụng do đợt test tạo/sửa/xóa: **0**. Không cần cleanup.
Không khẳng định 10 tình huống nghiệp vụ ghi đã pass trên database nhóm.

## Chiến lược cho lần kiểm thử ghi sau (chưa thực hiện)

1. Xác nhận backup phục hồi được và quyền ghi mới; kiểm tra ảnh hưởng website.
2. Ưu tiên database local/tách biệt có cùng schema thực tế. Xe test cần Available
   để duyệt, nhưng hiện schema không có cờ test: xe đã commit có thể xuất hiện
   trên website. Nếu không bảo đảm ẩn dữ liệu test, tiếp tục skip shared write.
3. Không chạy fixture cũ. Mỗi run phải có run UUID, manifest các UUID thực sự tạo,
   email test dùng miền .invalid và biển số duy nhất hợp lệ; không lấy ID thật.
4. Mọi UPDATE/DELETE phải lọc UUID trong manifest cùng marker test tương ứng;
   không dùng xóa theo thời gian, prefix đơn lẻ hoặc UPDATE cả bảng.
5. Ưu tiên rollback cho probe trong một transaction. Repository hiện tự mở và
   commit transaction, nên không thể bọc nó bằng transaction ngoài một cách
   tùy tiện; seed chưa commit cũng không thấy được từ connection khác.
6. Test đồng thời cần seed riêng đã commit, hai connection chỉ khóa đúng xe test,
   có timeout và barrier; không SELECT toàn bộ vehicle FOR UPDATE như fixture cũ.
7. Cleanup kiểm tra lại ownership/marker và FK, xóa đúng booking test rồi vehicle,
   account_role, account trong transaction. Nếu có tham chiếu ngoài manifest,
   dừng và báo cáo, không cascade/xóa bản ghi bên ngoài để ép cleanup.
8. Ghi nhận manifest trước seed, xác minh mọi ID đã biến mất sau cleanup; nếu
   process chết phải báo các ID còn lại. Không ghi secret vào manifest/log.

## Điều kiện trước VNPay

Có thể tiếp tục thiết kế adapter độc lập, nhưng chưa đủ bằng chứng xác nhận
Booking Approval sẵn sàng triển khai VNPay trên website: cần chạy các ca ghi và
đồng thời trên DB cô lập, review khác biệt trigger, chốt TTL giữ chỗ/audit và quy
tắc tiền đến muộn. Chưa thêm Payment API, VNPay hay Wallet ở 2B.1.
