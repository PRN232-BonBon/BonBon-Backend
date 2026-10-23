# Giai đoạn 2E.2 — PostgreSQL migration và Payment integration

## Kết quả

Đã kiểm thử trên PostgreSQL 16.15 của nhóm qua Tailscale, chỉ ghi database riêng
**bonbon_payment_test**, không ghi database chính bonbon. Đã áp dụng và kiểm tra
004 cùng proposal 005 trên database test. Không có lệnh DROP DATABASE trong
fixture mới hoặc các thao tác đã chạy. Không dùng lại DisposablePostgresDatabase.

- Full suite: **180 passed, 0 failed, 15 skipped**, tổng 195, khoảng 3 phút 7 giây.
  Gồm 156 unit/API/safety có sẵn và 24 nhóm PostgreSQL integration mới.
- Sau khi bổ sung chốt kiểm tra quyền role ngay cả trong lần provision đầu,
  build lại và chạy riêng 2 test schema/manifest: **2 passed, 0 failed**. Đây là
  lượt kiểm tra riêng có overlap, không cộng thành 182 tests độc lập.
- Build Release: **0 warning, 0 error**. git diff --check không có lỗi.
- Probe quyền chỉ đọc ban đầu: 1 passed, chạy riêng trước mọi thao tác ghi.
- Không chạy Frontend/browser lại vì không sửa Frontend; các kết quả 63/36 từ
  giai đoạn trước không được tính là kiểm thử lại trong đợt này.

## An toàn và định danh

Đầu đợt: cả hai repository feature/payment, giữ nguyên các thay đổi chưa commit
của các giai đoạn trước. Không commit/push hoặc thay đổi main.

Probe chỉ đọc ban đầu xác nhận:
- database=bonbon, current_user=bonbon_app, transaction_read_only=on;
- host được cấu hình cố định <approved-db-host-in-local-config>:5432;
- inet_server_addr=<server-address-retained-locally>, PostgreSQL=16.15;
- quyền provisioning được xác minh riêng; kết quả chi tiết giữ local;
- bonbon_payment_test chưa tồn tại.

Kết nối quản trị mới vào **postgres**, kiểm tra current_database/current_user,
địa chỉ/port/version và system_identifier khớp kết nối nguồn trước CREATE ROLE,
CREATE DATABASE và COMMENT database. Không thực hiện các lệnh này qua bonbon.

Tạo role **bonbon_payment_test_runner**: LOGIN, NOSUPERUSER, NOCREATEDB,
NOCREATEROLE, NOINHERIT, connection limit 4; là owner của database test. Không
GRANT hoặc REVOKE quyền nào trên bonbon. Kiểm tra catalog chỉ đọc chứng minh role
không có INSERT/UPDATE/DELETE/TRUNCATE/TRIGGER/REFERENCES trên bất kỳ bảng nghiệp
vụ nào trong schema bonbon của database chính, kể cả quyền có thể đến từ PUBLIC.
Role vẫn có thể có CONNECT theo mặc định PUBLIC; điều đó không đồng nghĩa quyền
đọc/ghi bảng nghiệp vụ và không được mô tả là đã REVOKE CONNECT trên database chính.

Mỗi connection dùng để ghi/repository đều được mở và xác minh database=test,
current_user=test runner, owner, địa chỉ/port/version và marker database trước
khi giao cho code có thể ghi. PostgreSQL không chuyển database trong cùng một
connection. Pooling=false, parameter/error detail logging=false, timeout ngắn.
Chỉ hai request cạnh tranh tại một thời điểm, collection không chạy song song.

Password test được sinh ngẫu nhiên trong bộ nhớ, không in ra log hoặc hardcode;
được lưu trong `.payment-test.local.json` cạnh secret config local để tái sử dụng
database đã đánh dấu. Pattern Git ignore đã được kiểm tra, không copy vào test
output/appsettings. Trước CREATE ROLE có kiểm tra các cấu hình statement/duration/
pgaudit logging để không đưa credential DDL vào log đó. Không thay đổi thiết lập
logging server. Secret VNPay trong test là synthetic vector, không phải credential
sandbox/production thực, không có request tới VNPay.

Database đã tồn tại chỉ được tái sử dụng khi owner, marker, credential local,
schema hash, control stage và manifest khớp. Database/role không nhận diện được
thì dừng, không overwrite/stash/drop. Trạng thái setup gián đoạn chỉ được tiếp tục
khi đúng marker/owner và không có dữ liệu ứng dụng; không xóa hoặc dựng lại bảng.

## Sao chép schema và kiểm thử migration

Không có pg_dump local nên dựng baseline consolidated 001 trên database test,
đối chiếu catalog thực tế và áp lại **7 function definitions** cùng nullability
đọc từ nguồn: has_active_role, check_actor_eligibility, check_message_participant,
protect_vehicle_owner, protect_booking_quote, check_vehicle_schedule, create_booking.
Không thực thi file 002/003 tuần tự. Role 1/2/3 là reference data cố định từ 001;
không sao chép account, contact, password hash, Booking, Payment hoặc PII từ nguồn.

Trước 004, fingerprint metadata giữa bản sao và nguồn phải giống nhau: columns,
types, nullability, defaults/generated expressions, constraint definitions, index
definitions, trigger definitions/enabled state và function definitions trong
schema bonbon. Owner/ACL không sao chép vì chủ đích sử dụng tài khoản test riêng.
Đây là bản sao cấu trúc tương đương được đối chiếu catalog, không phải bản dump
dữ liệu. SHA256 metadata nguồn trước/sau:

<schema-fingerprint-retained-locally>

Một lỗi SELECT 1/0 được thêm vào bản SQL trong bộ nhớ ngay trước COMMIT 004,
chỉ chạy trong test DB: transaction abort, ROLLBACK, metadata trở lại baseline và
không còn marker 004. Sau đó chạy file 004 thật thành công, kiểm tra 9 cột mới,
types/defaults, CHECK validated, audit FK/unique, 4 index unique/valid/ready với
đúng keys/predicates và marker version=1. Thử chạy lại 004 phải báo P0001 và giữ
nguyên schema đã commit. Không thực thi 004 trên database khác.

004 không sửa SQL nghiệp vụ trong đợt này, chỉ cập nhật header ghi nhận đã test
trên database riêng và chưa áp dụng lên bonbon.

## Những lỗi/khác biệt thực tế phát hiện

1. **Nullability auth drift:** account.password_hash ở nguồn cho phép NULL, còn
   baseline 001 yêu cầu NOT NULL. Chốt fingerprint đã dừng trước 004. Chỉ sửa bản
   sao test theo nullability catalog nguồn; không sửa 001 hoặc database chính.
2. **Catalog marker trong test harness:** comment của pg_database là shared object,
   phải dùng shobj_description(), không phải obj_description(). Cách cũ trả NULL
   và Npgsql GetString báo InvalidCastException, khiến setup dừng trước schema/
   migration. Đã sửa fixture/probe; không phải lỗi PaymentRepository.
3. **Quote guard drift vẫn có trên nguồn:** protect_booking_quote khớp 002, thiếu
   customer immutability/return-to-Pending guard; cho đổi quote ở Rejected/Cancelled.
   Đã sao chép đúng bản yếu sang test trước khi kiểm thử proposal 005. Không thay
   đổi function hoặc trigger nào trong bonbon.
4. Không phát hiện lỗi runtime PostgreSQL/Npgsql trong PaymentRepository,
   BookingRepository, PaymentService hoặc gateway qua các ca đã chạy. Không cần
   sửa code API/Business/DataAccess của ứng dụng.

Hai lần setup đầu thất bại do chốt kiểm tra marker/schema ở trên, đã được sửa
và chạy lại thành công; không bị che là các lượt test passed. Database/role đã
tạo được giữ lại, không bị xóa. Các lỗi 23505/23514/P0001 và IPN 99 trong test
constraint/rollback là kết quả mong đợi, không phải lỗi còn tồn tại của suite.

## Coverage đã chạy trên PostgreSQL thật

24 nhóm trong PaymentPostgresTests kiểm tra:
- 004 metadata, pre-commit rollback, rerun bị từ chối, keys/predicates/type/CHECK.
- Approve/Reject, missing Booking, không phải owner, replay, trạng thái quyết định.
- Hai Booking trùng lịch chỉ một được duyệt; account_role owner/customer và xe
  không hoạt động bị kiểm tra lại; quote/Create qua function 5 tham số/mine cũ.
- Create Payment từ quoted_rental_amount; cùng key replay; key khác bị chặn;
  hai request cùng key trả một attempt, khác key chỉ một thắng.
- Booking Pending chưa payable; Customer khác không tạo/xem payment; Role inactive.
- IPN success commit Payment/Booking/audit; exact duplicate kể cả đồng thời chỉ
  có một notification và không xác nhận Booking nhiều lần.
- Failed/Cancelled; success sau terminal phải đối soát; failure sau success không
  hạ trạng thái; transaction mới trên attempt đã success vẫn giữ bằng chứng audit.
- Sai signature, unknown reference, amount sai, transaction number sai định dạng;
  Return chỉ đọc, không thêm audit hoặc Confirmed.
- Late receipt; uncertain result/expiry chặn checkout mới; provider transaction
  dùng lại ở Booking khác không Confirmed, ghi PROVIDER_TRANSACTION_REUSED.
- Unique active/idempotency/accepted_success/transaction thực sự từ chối row xung
  đột; tiền vượt giới hạn hoặc TTL 31 phút bị CHECK từ chối.
- Trigger tức thời trên **một UUID Booking test** làm UPDATE Confirmed lỗi sau
  INSERT audit và UPDATE Payment: cả Payment, Booking, audit cùng rollback; IPN
  trả 99. Bỏ trigger/function test trong finally, retry cùng callback thành công.
  Đây là statement failure nhẹ trong DB test, không crash/cancel server hoặc tải.
- Guard 005 cấm thay customer/vehicle, cấm đổi giá cùng quyết định, cấm sửa quote/
  thời gian sau Rejected hoặc quay về PendingApproval; approve/reject giữ giá.
- Manifest xác minh dữ liệu do fixture tạo và Wallet/WalletTransaction vẫn rỗng.

## Proposal 005

`database/proposals/005_booking_quote_guard.sql` giữ nguyên tên/signature, owner,
ACL và trigger; chỉ CREATE OR REPLACE function trong transaction có timeout,
preflight yêu cầu đúng enabled trigger liên kết function. Customer/vehicle bất
biến luôn; không quay lại PendingApproval sau quyết định; quote/time chỉ có thể
đổi nếu cả OLD và NEW còn PendingApproval. Như vậy chặn cả sửa giá đồng thời với
PendingApproval → AwaitingPayment mà guard 001 hiện tại chưa chặn.

Đã áp dụng và kiểm thử trên test DB. **Chưa phê duyệt/triển khai lên bonbon.**
Trước rollout phải lưu function definition/owner/ACL/hash cũ, xác nhận body nguồn
đã biết, review nghiệp vụ sửa Booking Pending và kiểm tra backup/rollback riêng.
Preflight 005 hiện kiểm tra linkage/enable, chưa tự whitelist hash mọi body nguồn.
Không coi guard này là máy trạng thái đầy đủ của Booking.

## Dữ liệu giữ lại và skipped

Snapshot cuối sau suite và probe chỉ đọc:

| Bảng | Số hàng test |
| --- | ---: |
| account | 141 |
| vehicle | 47 |
| booking | 95 |
| payment | 34 |
| payment_notification | 30 |
| public.payment_test_manifest | 283 |
| wallet / wallet_transaction | 0 / 0 |

Các hàng tích lũy từ các lượt test đều synthetic; account email dạng UUID@test.invalid,
biển số TEST-..., không phải account đăng nhập thật. Manifest lưu run_id/kind/UUID
cho toàn bộ account/vehicle/booking; Payment và audit liên kết qua FK tới các UUID
đó. Giữ lại để inspect, không cleanup/xóa hàng hoặc DROP database. Trigger/function
fault test đã được dọn trong test DB. Mọi connection đóng khi test kết thúc.

15 skipped trong full suite:
- 10 nhóm cũ dùng fixture tự CREATE/DROP database: không enable trên server nhóm;
  các ca quan trọng đã được chuyển thành fixture test DB giữ lại riêng.
- 4 shared opt-in read-only tests không bật trong full suite; probe quyền ban đầu
  chạy riêng và fixture có đọc/hash catalog nguồn trước/sau bằng read-only session.
- 1 nhóm server crash/disk/network fault và sustained load: cần server độc lập,
  tuyệt đối chưa chạy trên server dùng chung. Cũng chưa test restore/PITR thật.

## Chạy lại có kiểm soát

Chỉ chạy khi vẫn được cho phép ghi database test. Biến môi trường của tiến trình:
- BONBON_PAYMENT_TEST_CONFIG_FILE: đường dẫn secret config local được cung cấp rõ,
  không copy password/connection string vào lệnh, source code hoặc log.
- BONBON_PAYMENT_TEST_CONSENT=I_CONFIRM_BONBON_PAYMENT_TEST_ONLY
- Xóa BONBON_TEST_POSTGRES_ADMIN/CONSENT để fixture cũ không bao giờ được enable.
- Không tự bật BONBON_SHARED_POSTGRES_* nếu chỉ muốn chạy test DB.

Build Release rồi dotnet test --no-build --no-restore -c Release. Không có config/
consent thì PaymentPostgresFact skipped, fixture không provision hoặc connect.
Không sửa file credential hoặc marker để ép dùng database không xác minh được.

## Điều kiện tiếp theo cho sandbox end-to-end

Đã đủ bằng chứng để **chuẩn bị sandbox E2E trên database test**, chưa đủ để bật
Payment trên bonbon/production. Cần:
1. Một instance API local/staging riêng trỏ đúng bonbon_payment_test bằng role test,
   JWT/auth configuration và account đăng nhập test thực (seed hiện có không có
   password hash đăng nhập hợp lệ); không đổi kết nối website đang chạy.
2. Merchant/secret VNPay sandbox thực trong secret local, HTTPS IPN/Return public,
   FrontendResultUrl cố định, CORS và SPA fallback; kiểm thử network/signature/IPN
   với gateway thật trong sandbox. Không tái sử dụng các checkout URL synthetic.
3. Chốt TTL giữ chỗ Booking, QUERYDR/đối soát, refund/extra receipt và multi-provider
   trước vận hành; schema guard runtime chưa xác minh đầy đủ mọi constraint/index.
4. Trước khi áp dụng 004/005 lên bonbon: approval riêng, backup restore được, kế
   hoạch DDL lock/maintenance và quyền runtime ít đặc quyền. Test role không phải
   cách khắc phục tài khoản bonbon_app hiện có quyền superuser trên server.

Payment flag trong appsettings/Compose vẫn false; Enabled=true chỉ được inject
trong service của integration tests với synthetic gateway options. Không có giao
dịch tiền/VNPay thật, không thay đổi Frontend, không commit/push/main.

## File thay đổi trong đợt 2E.2

- .gitignore — ignore credential test local.
- BonBon.Tests/BonBon.Tests.csproj — copy SQL 004/005 vào output để test.
- BonBon.Tests/Integration/SharedPostgresReadOnlyTests.cs — probe permissions/identity,
  đúng shared-object comment catalog.
- BonBon.Tests/Integration/PaymentPostgresFixture.cs — mới, guard/provision/clone/
  manifest, không DROP database.
- BonBon.Tests/Integration/PaymentPostgresTests.cs — mới, 24 nhóm DB + 1 skipped.
- database/proposals/004_vnpay_sandbox.sql — chỉ cập nhật header kết quả test.
- database/proposals/005_booking_quote_guard.sql — proposal mới, chỉ apply test DB.
- docs/payment-postgres-tests-2e2.md — báo cáo này.
