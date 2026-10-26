-- Crea los usuarios con login de la base y los asocia a los roles de grupo.
-- Se corre una vez por servidor, como superusuario, DESPUÉS de aplicar las migraciones.
-- Las contraseñas por defecto sirven sólo para el entorno local:
--
--   psql -v api_pass=... -v worker_pass=... -f deploy/crear-roles.sql
\if :{?api_pass}
\else
    \set api_pass sg_api_local
\endif
\if :{?worker_pass}
\else
    \set worker_pass sg_worker_local
\endif

SELECT format('CREATE ROLE sg_api_login LOGIN NOBYPASSRLS IN ROLE sg_api PASSWORD %L', :'api_pass')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'sg_api_login') \gexec

SELECT format('CREATE ROLE sg_worker_login LOGIN NOBYPASSRLS IN ROLE sg_worker PASSWORD %L', :'worker_pass')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'sg_worker_login') \gexec
