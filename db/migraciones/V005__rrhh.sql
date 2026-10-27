-- V005: recursos humanos (legajos, licencias y documentación con vencimiento).
--
-- Geclisa no tiene módulo de personal: este esquema no tiene equivalente que migrar.
-- No liquida sueldos; eso queda para un sistema de liquidación externo.
--
-- Un profesional que además es empleado tiene UN legajo enlazado a catalogos.profesionales:
-- catálogos sigue siendo el dueño de los datos asistenciales (matrícula, especialidades) y
-- este esquema guarda la relación laboral. No se duplica la persona.
--
-- Datos personales del personal (Ley 25.326): sólo los roles rrhh y administrador los leen,
-- y cada lectura queda en la auditoría. Las licencias por enfermedad son datos de salud
-- (dato sensible, art. 2): se guarda el tipo de licencia, nunca el diagnóstico.

-- Siempre definida, aunque V004 ya la crea: el esquema no debe depender del orden de otros módulos.
CREATE EXTENSION IF NOT EXISTS btree_gist;

CREATE SCHEMA rrhh;
GRANT USAGE ON SCHEMA rrhh TO sg_api;

-- Sectores de trabajo del personal (enfermería, administración, mantenimiento...).
-- Son distintos de los sectores de internación de Geclisa (Sectores = áreas con camas).
CREATE TABLE rrhh.sectores (
    id          uuid PRIMARY KEY,
    empresa_id  uuid NOT NULL REFERENCES nucleo.empresas (id),
    nombre      text NOT NULL CHECK (length(trim(nombre)) > 0),
    activo      boolean NOT NULL DEFAULT true,
    creado_en   timestamptz NOT NULL DEFAULT now(),
    UNIQUE (empresa_id, id),
    UNIQUE (empresa_id, nombre)
);

-- Puestos o funciones (enfermero/a, administrativo/a, mucama, médico/a de guardia...).
CREATE TABLE rrhh.puestos (
    id          uuid PRIMARY KEY,
    empresa_id  uuid NOT NULL REFERENCES nucleo.empresas (id),
    nombre      text NOT NULL CHECK (length(trim(nombre)) > 0),
    activo      boolean NOT NULL DEFAULT true,
    creado_en   timestamptz NOT NULL DEFAULT now(),
    UNIQUE (empresa_id, id),
    UNIQUE (empresa_id, nombre)
);

-- Legajo del personal.
CREATE TABLE rrhh.empleados (
    id                uuid PRIMARY KEY,
    empresa_id        uuid NOT NULL REFERENCES nucleo.empresas (id),
    legajo            int NOT NULL CHECK (legajo > 0),
    apellidos         text NOT NULL CHECK (length(trim(apellidos)) > 0),
    nombres           text NOT NULL CHECK (length(trim(nombres)) > 0),
    cuil              varchar(11) NOT NULL CHECK (cuil ~ '^[0-9]{11}$'),
    fecha_nacimiento  date NOT NULL,
    sexo_registral    char(1) NOT NULL CHECK (sexo_registral IN ('F', 'M', 'X')),
    email             text,
    telefono          text,
    domicilio         text,
    -- Relación laboral.
    modalidad         text NOT NULL CHECK (modalidad IN ('RELACION_DEPENDENCIA', 'MONOTRIBUTO', 'RESIDENCIA', 'PASANTIA', 'OTRA')),
    convenio          text,
    categoria         text,
    horas_semanales   smallint CHECK (horas_semanales BETWEEN 1 AND 72),
    sector_id         uuid NOT NULL,
    puesto_id         uuid NOT NULL,
    fecha_ingreso     date NOT NULL,
    fecha_egreso      date,
    motivo_egreso     text,
    -- Enlaces: el profesional de catálogos (si es médico u otro profesional de la salud) y su usuario del sistema.
    profesional_id    uuid,
    usuario_id        uuid REFERENCES nucleo.usuarios (id),
    version           int NOT NULL DEFAULT 1,
    creado_en         timestamptz NOT NULL DEFAULT now(),
    creado_por        uuid NOT NULL REFERENCES nucleo.usuarios (id),
    modificado_en     timestamptz,
    modificado_por    uuid REFERENCES nucleo.usuarios (id),
    FOREIGN KEY (empresa_id, sector_id) REFERENCES rrhh.sectores (empresa_id, id),
    FOREIGN KEY (empresa_id, puesto_id) REFERENCES rrhh.puestos (empresa_id, id),
    FOREIGN KEY (empresa_id, profesional_id) REFERENCES catalogos.profesionales (empresa_id, id),
    UNIQUE (empresa_id, id),
    UNIQUE (empresa_id, legajo),
    -- Una persona, un legajo por empresa. Un reingreso reactiva el legajo existente.
    CONSTRAINT uq_empleados_cuil UNIQUE (empresa_id, cuil),
    CONSTRAINT uq_empleados_profesional UNIQUE (empresa_id, profesional_id),
    CHECK (fecha_egreso IS NULL OR fecha_egreso >= fecha_ingreso),
    CHECK ((fecha_egreso IS NULL) = (motivo_egreso IS NULL))
);
CREATE INDEX ix_empleados_apellidos ON rrhh.empleados (empresa_id, lower(apellidos));

-- Licencias y ausencias. Fechas de calendario, ambos extremos incluidos.
CREATE TABLE rrhh.licencias (
    id              uuid PRIMARY KEY,
    empresa_id      uuid NOT NULL,
    empleado_id     uuid NOT NULL,
    tipo            text NOT NULL CHECK (tipo IN (
                        'VACACIONES', 'ENFERMEDAD', 'ACCIDENTE_TRABAJO', 'MATERNIDAD', 'NACIMIENTO',
                        'MATRIMONIO', 'FALLECIMIENTO_FAMILIAR', 'EXAMEN', 'SIN_GOCE', 'OTRA')),
    desde           date NOT NULL,
    hasta           date NOT NULL,
    periodo         daterange GENERATED ALWAYS AS (daterange(desde, hasta, '[]')) STORED,
    estado          text NOT NULL DEFAULT 'SOLICITADA' CHECK (estado IN ('SOLICITADA', 'APROBADA', 'RECHAZADA', 'ANULADA')),
    -- Texto administrativo. No es lugar para diagnósticos.
    observaciones   text,
    motivo_rechazo  text,
    resuelta_en     timestamptz,
    resuelta_por    uuid REFERENCES nucleo.usuarios (id),
    version         int NOT NULL DEFAULT 1,
    creado_en       timestamptz NOT NULL DEFAULT now(),
    creado_por      uuid NOT NULL REFERENCES nucleo.usuarios (id),
    FOREIGN KEY (empresa_id, empleado_id) REFERENCES rrhh.empleados (empresa_id, id),
    UNIQUE (empresa_id, id),
    CHECK (hasta >= desde),
    CHECK ((estado IN ('APROBADA', 'RECHAZADA')) = (resuelta_en IS NOT NULL)),
    CHECK (estado <> 'RECHAZADA' OR motivo_rechazo IS NOT NULL),
    -- Una persona no puede tener dos licencias vigentes (pedidas o aprobadas) que se pisen.
    CONSTRAINT ex_licencias_superpuestas EXCLUDE USING gist (
        empresa_id WITH =, empleado_id WITH =, periodo WITH &&
    ) WHERE (estado IN ('SOLICITADA', 'APROBADA'))
);
CREATE INDEX ix_licencias_periodo ON rrhh.licencias USING gist (empresa_id, periodo);

-- Documentación con vencimiento: matrícula, seguro de mala praxis, apto médico, vacunas.
-- Un documento renovado es una fila nueva; las anteriores quedan como historia.
CREATE TABLE rrhh.documentos (
    id           uuid PRIMARY KEY,
    empresa_id   uuid NOT NULL,
    empleado_id  uuid NOT NULL,
    tipo         text NOT NULL CHECK (tipo IN (
                     'MATRICULA', 'SEGURO_MALA_PRAXIS', 'APTO_MEDICO', 'VACUNACION', 'CAPACITACION', 'OTRO')),
    descripcion  text NOT NULL CHECK (length(trim(descripcion)) > 0),
    numero       text,
    emitido      date,
    vence        date,
    creado_en    timestamptz NOT NULL DEFAULT now(),
    creado_por   uuid NOT NULL REFERENCES nucleo.usuarios (id),
    FOREIGN KEY (empresa_id, empleado_id) REFERENCES rrhh.empleados (empresa_id, id),
    CHECK (vence IS NULL OR emitido IS NULL OR vence >= emitido)
);
CREATE INDEX ix_documentos_empleado ON rrhh.documentos (empresa_id, empleado_id, tipo, vence);

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['sectores', 'puestos', 'empleados', 'licencias', 'documentos']
    LOOP
        EXECUTE format('ALTER TABLE rrhh.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE rrhh.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format($p$CREATE POLICY p_%s_empresa ON rrhh.%I
                          USING (empresa_id = nucleo.empresa_actual())
                          WITH CHECK (empresa_id = nucleo.empresa_actual())$p$, t, t);
    END LOOP;
END
$$;

-- Sin DELETE: un legajo se da de baja con fecha de egreso, una licencia se anula y un documento se reemplaza.
GRANT SELECT, INSERT, UPDATE ON rrhh.sectores, rrhh.puestos, rrhh.empleados, rrhh.licencias TO sg_api;
GRANT SELECT, INSERT ON rrhh.documentos TO sg_api;
