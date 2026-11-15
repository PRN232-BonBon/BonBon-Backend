# Integration review — BonBon-Backend

Ngày 16/11/2026. Nhánh feature/sepay-payment-integration từ origin/main 7f7e6aaf5758cee2c63196324bc5ed946dee1001. Worktree mới: D:\School\PRN232\BonBon\worktrees\BonBon-Backend-sepay. Worktree cũ giữ nguyên tại D:\School\PRN232\BonBon\BonBon-Backend. Không copy secret, không checkout/reset/stash code cũ.

## Kết quả

dotnet restore/build Release PASS. dotnet test: 343 passed, 0 failed, 54 skipped (database opt-in không bật). Lifecycle runner in-memory của main PASS.

Tích hợp patch có chọn lọc; không merge/rebase nhánh review cũ. Auth/JWT/Vehicle/Tracking/owner portal mới của nhóm giữ nguyên. Backend main mới có 19 file thay đổi, Frontend 39 file; không sửa các file đó ngoài 2 trang Payment được review riêng.

PaymentModal.tsx: patch cũ không khớp context mới, đã thêm các hunk nhỏ trên bản main, giữ các câu chữ/cải tiến của nhóm. Simulator chỉ render khi DEV + loopback + flag local; build mặc định/staging tắt. PaymentResultPage.tsx: patch URL validation áp dụng được, giữ UI/main. Không thêm UI SePay. Frontend hiện chỉ checkout VNPay legacy, không tự chọn SePay hoặc giả thành công khi Backend tắt.

006_momo_sandbox.sql giữ nguyên prerequisite schema/audit cho 007 và dữ liệu đã áp dụng; không có gateway/API/DI MoMo hay ZaloPay. 004/005/006/007 không tự chạy; migration 010_optional_password_hash.sql mới của nhóm giữ nguyên, hướng dẫn DBA đã bổ sung bước review trước seed staging. Chưa kiểm thử schema mới trên PostgreSQL vì không tạo/copy secret hoặc truy cập database trong lượt này.

Quét source mới không khớp giá trị credential local đang có; không có private/.env/bin/obj/dist/cache trong commit candidates. Không sao chép secret vào worktree mới. Cấu hình SePay và readiness mặc định false. Docker/CI/CD production không sửa; backend workflow chỉ push main, manual dispatch vẫn có khả năng deploy production nên không gọi workflow. Feature push không tự chạy production workflow theo cấu hình hiện tại.

## Rủi ro credential lịch sử — còn chặn merge/deploy

Lịch sử public đã có DB password/JWT key tại BonBon.API/appsettings.Development.json và BonBon.API/appsettings.json (historical commit 3e7a1b93e9a50be433645c722bff4a8019e62827). Không ghi lại giá trị. Người dùng xác nhận CHƯA xác nhận rotate/revoke. Người dùng đã cho phép riêng commit/push FEATURE BRANCH để nhóm review dù chưa xác nhận rotate/revoke, với điều kiện không có secret mới trong source/staged diff. Đây không phải xác nhận đã xử lý credential lịch sử. Quản trị viên vẫn phải rotate/revoke và xác nhận phạm vi ảnh hưởng trước merge/deploy; không tự rewrite history hoặc rotate.

Commit được người dùng cho phép: feat(payment): integrate SePay sandbox backend and staging. Chỉ push nhánh feature, không tạo PR/merge/deploy.

## Trước merge/deploy

Cần staging DNS/TLS/database cô lập, IPN secret riêng, validation Caddy/Compose, preflight 004–007 và review 010, xác nhận timezone/response thực tế, Frontend form POST SePay và E2E do người dùng thao tác. Chưa có TTL/reconciliation worker/refund. Có thể review code tại worktree mới; chưa đủ điều kiện merge/deploy.
