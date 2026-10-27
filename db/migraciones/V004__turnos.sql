-- V004: turnos ambulatorios.
--
-- Diferencias deliberadas con Geclisa (ver docs/turnos-geclisa.md):
--   * La base impide el doble turno con restricciones de exclusión, no con un SELECT COUNT previo.
--   * Un profesional no puede tener dos turnos superpuestos aunque sean de servicios o consultorios distintos.
--   * Un turno cancelado no se borra: cambia de estado y queda el motivo.
--   * El turno no copia los datos del paciente: los referencia.
CREATE EXTENSION IF NOT EXISTS btree_gist;

CREATE SCHEMA turnos;
GRANT USAGE ON SCHEMA turnos TO sg_api;

-- Agenda semanal de un profesional (Geclisa: Cronograma). Una fila por día de la semana y franja.
CREATE TABLE turnos.agendas (
    id                  uuid PRIMARY KEY,
    empresa_id          uuid NOT NULL,
    profesional_id      uuid NOT NULL,
    servicio_id         uuid NOT NULL,
    consultorio_id      uuid NOT NULL,
    -- 1 = lunes ... 7 = domingo (ISO 8601).
    dia_semana          smallint NOT NULL CHECK (dia_semana BETWEEN 1 AND 7),
    hora_desde          time NOT NULL,
    hora_hasta          time NOT NULL,
    duracion_minutos    smallint NOT NULL CHECK (duracion_minutos BETWEEN 5 AND 240),
    vigente_desde       date NOT NULL,
    vigente_hasta       date,
    -- Pacientes por horario (Geclisa: CantPacientesSimultaneos).
    capacidad           smallint NOT NULL DEFAULT 1 CHECK (capacidad BETWEEN 1 AND 20),
    -- Sobreturnos permitidos por horario, además de la capacidad.
    sobreturnos         smallint NOT NULL DEFAULT 0 CHECK (sobreturnos BETWEEN 0 AND 10),
    -- Quién da los turnos de esta agenda. Mientras convivan los dos sistemas, cada agenda tiene
    -- un solo dueño; el sistema nuevo no da turnos en agendas que todavía maneja Geclisa.
    gestionada_por      text NOT NULL DEFAULT 'SISTEMA_NUEVO' CHECK (gestionada_por IN ('SISTEMA_NUEVO', 'GECLISA')),
    activa              boolean NOT NULL DEFAULT true,
    geclisa_id          int,
    version             int NOT NULL DEFAULT 1,
    creado_en           timestamptz NOT NULL DEFAULT now(),
    creado_por          uuid NOT NULL REFERENCES nucleo.usuarios (id),
    modificado_en       timestamptz,
    modificado_por      uuid REFERENCES nucleo.usuarios (id),
    FOREIGN KEY (empresa_id, profesional_id) REFERENCES catalogos.profesionales (empresa_id, id),
    FOREIGN KEY (empresa_id, servicio_id) REFERENCES catalogos.servicios (empresa_id, id),
    FOREIGN KEY (empresa_id, consultorio_id) REFERENCES catalogos.consultorios (empresa_id, id),
    UNIQUE (empresa_id, id),
    UNIQUE (empresa_id, geclisa_id),
    -- Columnas derivadas para la restricción de abajo: minutos del día y período de vigencia.
    franja              int4range GENERATED ALWAYS AS (
                            int4range((extract(epoch FROM hora_desde) / 60)::int, (extract(epoch FROM hora_hasta) / 60)::int)
                        ) STORED,
    vigencia            daterange GENERATED ALWAYS AS (daterange(vigente_desde, vigente_hasta, '[]')) STORED,
    CHECK (hora_hasta > hora_desde),
    CHECK (vigente_hasta IS NULL OR vigente_hasta >= vigente_desde),
    -- Un profesional no puede tener dos agendas activas que se pisen el mismo día de la semana.
    CONSTRAINT ex_agendas_superpuestas EXCLUDE USING gist (
        empresa_id WITH =, profesional_id WITH =, dia_semana WITH =, franja WITH &&, vigencia WITH &&
    ) WHERE (activa)
);
CREATE INDEX ix_agendas_profesional ON turnos.agendas (empresa_id, profesional_id) WHERE activa;

-- Coberturas que acepta la agenda (Geclisa: CronogramaxOS). Sin filas: acepta todas.
CREATE TABLE turnos.agendas_coberturas (
    empresa_id    uuid NOT NULL,
    agenda_id     uuid NOT NULL,
    cobertura_id  uuid NOT NULL,
    PRIMARY KEY (agenda_id, cobertura_id),
    FOREIGN KEY (empresa_id, agenda_id) REFERENCES turnos.agendas (empresa_id, id),
    FOREIGN KEY (empresa_id, cobertura_id) REFERENCES catalogos.coberturas (empresa_id, id)
);

-- Períodos sin atención: licencias de un profesional o feriados de toda la empresa
-- (Geclisa: Feriados y Feriados_PrestadoresExcluidos).
CREATE TABLE turnos.bloqueos (
    id              uuid PRIMARY KEY,
    empresa_id      uuid NOT NULL REFERENCES nucleo.empresas (id),
    -- NULL: aplica a todos los profesionales.
    profesional_id  uuid,
    periodo         tstzrange NOT NULL CHECK (NOT isempty(periodo)),
    motivo          text NOT NULL CHECK (length(trim(motivo)) > 0),
    creado_en       timestamptz NOT NULL DEFAULT now(),
    creado_por      uuid NOT NULL REFERENCES nucleo.usuarios (id),
    FOREIGN KEY (empresa_id, profesional_id) REFERENCES catalogos.profesionales (empresa_id, id)
);
CREATE INDEX ix_bloqueos_periodo ON turnos.bloqueos USING gist (empresa_id, periodo);

-- Turnos (Geclisa: Turnos).
CREATE TABLE turnos.turnos (
    id                  uuid PRIMARY KEY,
    empresa_id          uuid NOT NULL,
    agenda_id           uuid NOT NULL,
    profesional_id      uuid NOT NULL,
    servicio_id         uuid NOT NULL,
    consultorio_id      uuid NOT NULL,
    paciente_id         uuid NOT NULL,
    horario             tstzrange NOT NULL CHECK (NOT isempty(horario) AND lower_inc(horario) AND NOT upper_inc(horario)),
    -- Lugar que ocupa dentro del horario: 1..capacidad son turnos normales, los siguientes son sobreturnos.
    cupo                smallint NOT NULL CHECK (cupo >= 1),
    es_sobreturno       boolean NOT NULL DEFAULT false,
    estado              text NOT NULL DEFAULT 'RESERVADO'
                        CHECK (estado IN ('RESERVADO', 'CONFIRMADO', 'PRESENTE', 'ATENDIDO', 'AUSENTE', 'CANCELADO')),
    cobertura_id        uuid,
    plan_id             uuid,
    numero_afiliado     text,
    observaciones       text,
    origen              text NOT NULL DEFAULT 'PRESENCIAL' CHECK (origen IN ('PRESENCIAL', 'TELEFONO', 'WEB', 'API')),
    motivo_cancelacion  text,
    cancelado_en        timestamptz,
    cancelado_por       uuid REFERENCES nucleo.usuarios (id),
    geclisa_id          int,
    version             int NOT NULL DEFAULT 1,
    creado_en           timestamptz NOT NULL DEFAULT now(),
    creado_por          uuid NOT NULL REFERENCES nucleo.usuarios (id),
    modificado_en       timestamptz,
    modificado_por      uuid REFERENCES nucleo.usuarios (id),
    FOREIGN KEY (empresa_id, agenda_id) REFERENCES turnos.agendas (empresa_id, id),
    FOREIGN KEY (empresa_id, profesional_id) REFERENCES catalogos.profesionales (empresa_id, id),
    FOREIGN KEY (empresa_id, servicio_id) REFERENCES catalogos.servicios (empresa_id, id),
    FOREIGN KEY (empresa_id, consultorio_id) REFERENCES catalogos.consultorios (empresa_id, id),
    FOREIGN KEY (empresa_id, paciente_id) REFERENCES pacientes.pacientes (empresa_id, id),
    FOREIGN KEY (empresa_id, cobertura_id) REFERENCES catalogos.coberturas (empresa_id, id),
    FOREIGN KEY (empresa_id, cobertura_id, plan_id) REFERENCES catalogos.planes (empresa_id, cobertura_id, id),
    UNIQUE (empresa_id, geclisa_id),
    CHECK (plan_id IS NULL OR cobertura_id IS NOT NULL),
    CHECK ((estado = 'CANCELADO') = (cancelado_en IS NOT NULL)),
    CHECK (estado <> 'CANCELADO' OR (motivo_cancelacion IS NOT NULL AND cancelado_por IS NOT NULL)),

    -- Doble turno: el mismo profesional no puede tener dos turnos activos superpuestos en el mismo cupo.
    -- Con capacidad 1 y sin sobreturnos, eso es "un paciente por horario".
    CONSTRAINT ex_turnos_profesional_cupo EXCLUDE USING gist (
        empresa_id WITH =, profesional_id WITH =, cupo WITH =, horario WITH &&
    ) WHERE (estado <> 'CANCELADO'),

    -- Un paciente no puede estar en dos turnos activos al mismo tiempo.
    CONSTRAINT ex_turnos_paciente EXCLUDE USING gist (
        empresa_id WITH =, paciente_id WITH =, horario WITH &&
    ) WHERE (estado <> 'CANCELADO')
);
CREATE INDEX ix_turnos_agenda_dia ON turnos.turnos USING gist (empresa_id, profesional_id, horario);
CREATE INDEX ix_turnos_paciente ON turnos.turnos (empresa_id, paciente_id, lower(horario));

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['agendas', 'agendas_coberturas', 'bloqueos', 'turnos']
    LOOP
        EXECUTE format('ALTER TABLE turnos.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE turnos.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format($p$CREATE POLICY p_%s_empresa ON turnos.%I
                          USING (empresa_id = nucleo.empresa_actual())
                          WITH CHECK (empresa_id = nucleo.empresa_actual())$p$, t, t);
    END LOOP;
END
$$;

GRANT SELECT, INSERT, UPDATE ON turnos.agendas, turnos.turnos TO sg_api;
GRANT SELECT, INSERT ON turnos.bloqueos TO sg_api;
-- Las coberturas de una agenda se reemplazan completas, por eso es la única tabla con DELETE.
GRANT SELECT, INSERT, DELETE ON turnos.agendas_coberturas TO sg_api;
