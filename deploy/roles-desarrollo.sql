-- Usuarios con login para el entorno LOCAL, con contraseñas conocidas.
-- Lo aplica el comando de migraciones con --datos-desarrollo. En un servidor real usar deploy/crear-roles.sql.
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'sg_api_login') THEN
        CREATE ROLE sg_api_login LOGIN NOBYPASSRLS PASSWORD 'sg_api_local';
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'sg_worker_login') THEN
        CREATE ROLE sg_worker_login LOGIN NOBYPASSRLS PASSWORD 'sg_worker_local';
    END IF;
END
$$;
GRANT sg_api TO sg_api_login;
GRANT sg_worker TO sg_worker_login;
