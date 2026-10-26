-- V001: núcleo multiempresa, auditoría inalterable y bandeja de eventos.
--
-- Roles de grupo (sin login). Los usuarios con login se crean en el despliegue
-- y se hacen miembros de estos grupos (ver deploy/crear-roles.sql).
--   sg_api     : la aplicación. Sujeta a seguridad por fila. Nunca borra.
--   sg_worker  : el publicador de eventos. Lee la bandeja de todas las empresas.
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'sg_api') THEN
        CREATE ROLE sg_api NOLOGIN NOBYPASSRLS;
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'sg_worker') THEN
        CREATE ROLE sg_worker NOLOGIN NOBYPASSRLS;
    END IF;
END
$$;

-- =====================================================================
-- Núcleo: empresas, usuarios y pertenencia
-- =====================================================================
CREATE SCHEMA nucleo;
GRANT USAGE ON SCHEMA nucleo TO sg_api, sg_worker;

-- Empresa en la que trabaja la transacción actual. NULL si no se fijó:
-- en ese caso las políticas de fila no devuelven nada.
CREATE FUNCTION nucleo.empresa_actual() RETURNS uuid
LANGUAGE sql STABLE AS $$
    SELECT nullif(current_setting('app.empresa_id', true), '')::uuid
$$;

CREATE FUNCTION nucleo.usuario_actual() RETURNS uuid
LANGUAGE sql STABLE AS $$
    SELECT nullif(current_setting('app.usuario_id', true), '')::uuid
$$;

GRANT EXECUTE ON FUNCTION nucleo.empresa_actual(), nucleo.usuario_actual() TO sg_api, sg_worker;

CREATE TABLE nucleo.empresas (
    id          uuid PRIMARY KEY,
    razon_social text NOT NULL,
    cuit        varchar(11) NOT NULL UNIQUE CHECK (cuit ~ '^[0-9]{11}$'),
    activa      boolean NOT NULL DEFAULT true,
    creado_en   timestamptz NOT NULL DEFAULT now()
);

-- Una persona, un usuario. El sujeto es el "sub" del proveedor de identidad (OpenID Connect).
CREATE TABLE nucleo.usuarios (
    id              uuid PRIMARY KEY,
    sujeto_oidc     text NOT NULL UNIQUE,
    nombre_mostrar  text NOT NULL,
    activo          boolean NOT NULL DEFAULT true,
    creado_en       timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE nucleo.usuarios_empresas (
    usuario_id  uuid NOT NULL REFERENCES nucleo.usuarios (id),
    empresa_id  uuid NOT NULL REFERENCES nucleo.empresas (id),
    roles       text[] NOT NULL DEFAULT '{}',
    activo      boolean NOT NULL DEFAULT true,
    PRIMARY KEY (usuario_id, empresa_id)
);

-- La API sólo lee el núcleo para resolver quién es el usuario y en qué empresas trabaja.
GRANT SELECT ON nucleo.empresas, nucleo.usuarios, nucleo.usuarios_empresas TO sg_api;

-- =====================================================================
-- Auditoría: sólo agregar (Ley 26.529, Ley 25.326, Decreto 393/2023)
-- =====================================================================
CREATE SCHEMA auditoria;
GRANT USAGE ON SCHEMA auditoria TO sg_api;

CREATE TABLE auditoria.eventos (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    empresa_id    uuid NOT NULL REFERENCES nucleo.empresas (id),
    usuario_id    uuid NOT NULL REFERENCES nucleo.usuarios (id),
    ocurrido_en   timestamptz NOT NULL DEFAULT now(),
    accion        text NOT NULL CHECK (accion IN ('LEER', 'BUSCAR', 'CREAR', 'MODIFICAR')),
    recurso_tipo  text NOT NULL,
    recurso_id    uuid,
    detalle       jsonb NOT NULL DEFAULT '{}',
    origen_ip     inet
);
CREATE INDEX ix_auditoria_recurso ON auditoria.eventos (empresa_id, recurso_tipo, recurso_id, ocurrido_en);
CREATE INDEX ix_auditoria_usuario ON auditoria.eventos (empresa_id, usuario_id, ocurrido_en);

CREATE FUNCTION auditoria.impedir_cambios() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'La tabla % es de sólo agregar: no se permite %', TG_TABLE_NAME, TG_OP
        USING ERRCODE = 'insufficient_privilege';
END
$$;

-- El disparador frena también al dueño de la tabla, no sólo a la aplicación.
CREATE TRIGGER tr_eventos_inalterables
    BEFORE UPDATE OR DELETE ON auditoria.eventos
    FOR EACH ROW EXECUTE FUNCTION auditoria.impedir_cambios();
CREATE TRIGGER tr_eventos_sin_truncate
    BEFORE TRUNCATE ON auditoria.eventos
    FOR EACH STATEMENT EXECUTE FUNCTION auditoria.impedir_cambios();

ALTER TABLE auditoria.eventos ENABLE ROW LEVEL SECURITY;
ALTER TABLE auditoria.eventos FORCE ROW LEVEL SECURITY;
CREATE POLICY p_eventos_empresa ON auditoria.eventos
    USING (empresa_id = nucleo.empresa_actual())
    WITH CHECK (empresa_id = nucleo.empresa_actual() AND usuario_id = nucleo.usuario_actual());

-- La API registra pero no lee la auditoría; la consulta será un módulo aparte con su propio permiso.
GRANT INSERT ON auditoria.eventos TO sg_api;

-- =====================================================================
-- Integración: bandeja de salida de eventos (patrón outbox)
-- =====================================================================
CREATE SCHEMA integracion;
GRANT USAGE ON SCHEMA integracion TO sg_api, sg_worker;

CREATE TABLE integracion.bandeja_salida (
    id             uuid PRIMARY KEY,
    empresa_id     uuid NOT NULL REFERENCES nucleo.empresas (id),
    tipo           text NOT NULL,
    version        int  NOT NULL DEFAULT 1,
    agregado_tipo  text NOT NULL,
    agregado_id    uuid NOT NULL,
    datos          jsonb NOT NULL,
    ocurrido_en    timestamptz NOT NULL DEFAULT now(),
    publicado_en   timestamptz,
    intentos       int NOT NULL DEFAULT 0,
    ultimo_error   text
);
CREATE INDEX ix_bandeja_pendientes ON integracion.bandeja_salida (ocurrido_en) WHERE publicado_en IS NULL;

ALTER TABLE integracion.bandeja_salida ENABLE ROW LEVEL SECURITY;
ALTER TABLE integracion.bandeja_salida FORCE ROW LEVEL SECURITY;
CREATE POLICY p_bandeja_api ON integracion.bandeja_salida TO sg_api
    USING (empresa_id = nucleo.empresa_actual())
    WITH CHECK (empresa_id = nucleo.empresa_actual());
-- El publicador trabaja sobre todas las empresas, pero sólo puede marcar entregas.
CREATE POLICY p_bandeja_worker ON integracion.bandeja_salida TO sg_worker
    USING (true);

GRANT INSERT ON integracion.bandeja_salida TO sg_api;
GRANT SELECT, UPDATE (publicado_en, intentos, ultimo_error) ON integracion.bandeja_salida TO sg_worker;
