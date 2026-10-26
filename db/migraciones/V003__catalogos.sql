-- V003: catálogos compartidos (profesionales, servicios, especialidades, sedes, consultorios, coberturas).
--
-- Son datos de referencia que usan varios módulos (turnos, historia clínica, facturación).
-- Excepción explícita a la regla de módulos: otros esquemas pueden tener FK hacia catalogos.*,
-- pero sólo el módulo de catálogos los escribe.
--
-- Cada tabla guarda el id equivalente de Geclisa (geclisa_id) para el puente de sincronización.

-- Zona horaria de cada empresa: las agendas se definen en hora local.
ALTER TABLE nucleo.empresas
    ADD COLUMN zona_horaria text NOT NULL DEFAULT 'America/Argentina/Buenos_Aires';

CREATE SCHEMA catalogos;
GRANT USAGE ON SCHEMA catalogos TO sg_api;

-- Servicios de atención (Geclisa: Servicios). Las agendas y los turnos se dan por servicio.
CREATE TABLE catalogos.servicios (
    id          uuid PRIMARY KEY,
    empresa_id  uuid NOT NULL REFERENCES nucleo.empresas (id),
    nombre      text NOT NULL CHECK (length(trim(nombre)) > 0),
    activo      boolean NOT NULL DEFAULT true,
    geclisa_id  int,
    creado_en   timestamptz NOT NULL DEFAULT now(),
    UNIQUE (empresa_id, id),
    UNIQUE (empresa_id, nombre),
    UNIQUE (empresa_id, geclisa_id)
);

-- Especialidades médicas (Geclisa: Especialidades).
CREATE TABLE catalogos.especialidades (
    id          uuid PRIMARY KEY,
    empresa_id  uuid NOT NULL REFERENCES nucleo.empresas (id),
    nombre      text NOT NULL CHECK (length(trim(nombre)) > 0),
    activo      boolean NOT NULL DEFAULT true,
    geclisa_id  int,
    creado_en   timestamptz NOT NULL DEFAULT now(),
    UNIQUE (empresa_id, id),
    UNIQUE (empresa_id, nombre),
    UNIQUE (empresa_id, geclisa_id)
);

-- Profesionales (Geclisa: Prestadores, sólo lo necesario para turnos e historia clínica).
CREATE TABLE catalogos.profesionales (
    id                    uuid PRIMARY KEY,
    empresa_id            uuid NOT NULL REFERENCES nucleo.empresas (id),
    apellidos             text NOT NULL CHECK (length(trim(apellidos)) > 0),
    nombres               text NOT NULL CHECK (length(trim(nombres)) > 0),
    matricula_nacional    text,
    matricula_provincial  text,
    -- Usuario del sistema con el que firma, si lo tiene.
    usuario_id            uuid REFERENCES nucleo.usuarios (id),
    activo                boolean NOT NULL DEFAULT true,
    geclisa_id            int,
    creado_en             timestamptz NOT NULL DEFAULT now(),
    UNIQUE (empresa_id, id),
    UNIQUE (empresa_id, geclisa_id),
    CHECK (matricula_nacional IS NOT NULL OR matricula_provincial IS NOT NULL)
);

CREATE TABLE catalogos.profesionales_especialidades (
    empresa_id       uuid NOT NULL,
    profesional_id   uuid NOT NULL,
    especialidad_id  uuid NOT NULL,
    PRIMARY KEY (profesional_id, especialidad_id),
    FOREIGN KEY (empresa_id, profesional_id) REFERENCES catalogos.profesionales (empresa_id, id),
    FOREIGN KEY (empresa_id, especialidad_id) REFERENCES catalogos.especialidades (empresa_id, id)
);

-- Sedes y consultorios (Geclisa: Ubicaciones y Consultorios).
CREATE TABLE catalogos.sedes (
    id          uuid PRIMARY KEY,
    empresa_id  uuid NOT NULL REFERENCES nucleo.empresas (id),
    nombre      text NOT NULL CHECK (length(trim(nombre)) > 0),
    direccion   text,
    activo      boolean NOT NULL DEFAULT true,
    creado_en   timestamptz NOT NULL DEFAULT now(),
    UNIQUE (empresa_id, id),
    UNIQUE (empresa_id, nombre)
);

CREATE TABLE catalogos.consultorios (
    id          uuid PRIMARY KEY,
    empresa_id  uuid NOT NULL,
    sede_id     uuid NOT NULL,
    nombre      text NOT NULL CHECK (length(trim(nombre)) > 0),
    activo      boolean NOT NULL DEFAULT true,
    geclisa_id  int,
    creado_en   timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (empresa_id, sede_id) REFERENCES catalogos.sedes (empresa_id, id),
    UNIQUE (empresa_id, id),
    UNIQUE (empresa_id, sede_id, nombre),
    UNIQUE (empresa_id, geclisa_id)
);

-- Coberturas y planes (Geclisa: ObrasSociales y Planes).
CREATE TABLE catalogos.coberturas (
    id          uuid PRIMARY KEY,
    empresa_id  uuid NOT NULL REFERENCES nucleo.empresas (id),
    nombre      text NOT NULL CHECK (length(trim(nombre)) > 0),
    sigla       text,
    tipo        text NOT NULL CHECK (tipo IN ('OBRA_SOCIAL', 'PREPAGA', 'PARTICULAR', 'OTRA')),
    activo      boolean NOT NULL DEFAULT true,
    geclisa_id  int,
    creado_en   timestamptz NOT NULL DEFAULT now(),
    UNIQUE (empresa_id, id),
    UNIQUE (empresa_id, nombre),
    UNIQUE (empresa_id, geclisa_id)
);

CREATE TABLE catalogos.planes (
    id            uuid PRIMARY KEY,
    empresa_id    uuid NOT NULL,
    cobertura_id  uuid NOT NULL,
    nombre        text NOT NULL CHECK (length(trim(nombre)) > 0),
    activo        boolean NOT NULL DEFAULT true,
    geclisa_id    int,
    creado_en     timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (empresa_id, cobertura_id) REFERENCES catalogos.coberturas (empresa_id, id),
    UNIQUE (empresa_id, id),
    -- Permite la FK compuesta (plan, cobertura) desde turnos: el plan debe ser de esa cobertura.
    UNIQUE (empresa_id, cobertura_id, id),
    UNIQUE (empresa_id, cobertura_id, nombre),
    UNIQUE (empresa_id, geclisa_id)
);

-- Seguridad por fila y permisos, igual para todas las tablas del esquema.
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['servicios', 'especialidades', 'profesionales', 'profesionales_especialidades',
                             'sedes', 'consultorios', 'coberturas', 'planes']
    LOOP
        EXECUTE format('ALTER TABLE catalogos.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE catalogos.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format($p$CREATE POLICY p_%s_empresa ON catalogos.%I
                          USING (empresa_id = nucleo.empresa_actual())
                          WITH CHECK (empresa_id = nucleo.empresa_actual())$p$, t, t);
        -- Sin DELETE: un dato de catálogo se desactiva, porque hay turnos e historias que lo referencian.
        EXECUTE format('GRANT SELECT, INSERT, UPDATE ON catalogos.%I TO sg_api', t);
    END LOOP;
END
$$;
