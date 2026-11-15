-- BonBon: tài khoản đăng nhập bằng Google không có mật khẩu local.
--
-- Vấn đề: password_hash NOT NULL buộc tài khoản tạo qua Google phải ghi một
-- chuỗi placeholder (ví dụ "Pass_xai_tam_de_demo_syabau"). Chuỗi đó không phải
-- BCrypt hash nên:
--   * BCrypt.Verify ném SaltParseException -> HTTP 500 thay vì lỗi nghiệp vụ.
--   * ChangePasswordAsync cũng ném lỗi tương tự.
--
-- Sửa: cho phép NULL = tài khoản chỉ đăng nhập bằng Google. API phải trả lỗi
-- nghiệp vụ "INVALID_CREDENTIALS" / "PASSWORD_NOT_SET" thay vì 500.

BEGIN;

-- 1. Cho phép NULL và bỏ CHECK "không rỗng" trên password_hash.
ALTER TABLE bonbon.account
    ALTER COLUMN password_hash DROP NOT NULL,
    DROP CONSTRAINT IF EXISTS account_password_hash_check;

-- 2. Dọn các placeholder đã ghi nhầm trước đây.
--    Chỉ xoá những giá trị KHÔNG phải BCrypt hash (BCrypt luôn bắt đầu bằng
--    "$2a$", "$2b$" hoặc "$2y$"), nên không đụng tới hash hợp lệ.
UPDATE bonbon.account
SET password_hash = NULL
WHERE password_hash IS NOT NULL
  AND password_hash NOT LIKE '$2%';

-- 3. CHECK mới: NULL (Google-only) hoặc chuỗi không rỗng.
ALTER TABLE bonbon.account
    ADD CONSTRAINT account_password_hash_check
    CHECK (password_hash IS NULL OR btrim(password_hash) <> '');

COMMENT ON COLUMN bonbon.account.password_hash IS
    'BCrypt hash; NULL = tài khoản chỉ đăng nhập bằng Google, không có mật khẩu local.';

COMMIT;
