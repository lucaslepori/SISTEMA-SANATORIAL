-- V002: módulo de pacientes (padrón único por empresa).
--
-- Reglas:
--   * Nada se borra: un paciente se da de baja (activo = false) o se unifica con otro.
--   * Cada cambio incrementa "version" (control de concurrencia optimista).
--   * Los identificadores externos (DNI, ficha de Geclisa, etc.) viven en una tabla aparte.
CREATE SCHEMA pacientes;
GRANT USAGE ON SCHEMA pacientes TO sg_api;

CREATE TABLE pacientes.pacientes (
    id                    uuid PRIMARY KEY,
    empresa_id            uuid NOT NULL REFERENCES nucleo.empresas (id),
    apellidos             text NOT NULL CHECK (length(trim(apellidos)) > 0),
    nombres               text NOT NULL CHECK (length(trim(nombres)) > 0),
    -- Ley 26.743: nombre de pila elegido, a usar en el trato aunque difiera del documento.
    nombre_autopercibido  text,
    fecha_nacimiento      date NOT NULL CHECK (fecha_nacimiento > date '1900-01-01'),
    -- Sexo registrado en el DNI: F, M o X (Decreto 476/2021).
    sexo_registral        char(1) NOT NULL CHECK (sexo_registral IN ('F', 'M', 'X')),
    telefono              text,
    email                 text,
    activo                boolean NOT NULL DEFAULT true,
    unificado_en_id       uuid REFERENCES pacientes.pacientes (id),
    version               int NOT NULL DEFAULT 1,
    creado_en             timestamptz NOT NULL DEFAULT now(),
    creado_por            uuid NOT NULL REFERENCES nucleo.usuarios (id),
    modificado_en         timestamptz,
    modificado_por        uuid REFERENCES nucleo.usuarios (id),
    UNIQUE (empresa_id, id)
);
CREATE INDEX ix_pacientes_apellidos ON pacientes.pacientes (empresa_id, lower(apellidos), lower(nombres));

CREATE TABLE pacientes.identificadores (
    id           uuid PRIMARY KEY,
    empresa_id   uuid NOT NULL,
    paciente_id  uuid NOT NULL,
    sistema      text NOT NULL CHECK (sistema IN ('DNI', 'PASAPORTE', 'CUIL', 'GECLISA_FICHA', 'OTRO')),
    valor        text NOT NULL CHECK (length(trim(valor)) > 0),
    creado_en    timestamptz NOT NULL DEFAULT now(),
    -- La FK compuesta impide colgar un identificador de un paciente de otra empresa.
    FOREIGN KEY (empresa_id, paciente_id) REFERENCES pacientes.pacientes (empresa_id, id),
    UNIQUE (empresa_id, sistema, valor)
);
CREATE INDEX ix_identificadores_paciente ON pacientes.identificadores (paciente_id);

ALTER TABLE pacientes.pacientes ENABLE ROW LEVEL SECURITY;
ALTER TABLE pacientes.pacientes FORCE ROW LEVEL SECURITY;
CREATE POLICY p_pacientes_empresa ON pacientes.pacientes
    USING (empresa_id = nucleo.empresa_actual())
    WITH CHECK (empresa_id = nucleo.empresa_actual());

ALTER TABLE pacientes.identificadores ENABLE ROW LEVEL SECURITY;
ALTER TABLE pacientes.identificadores FORCE ROW LEVEL SECURITY;
CREATE POLICY p_identificadores_empresa ON pacientes.identificadores
    USING (empresa_id = nucleo.empresa_actual())
    WITH CHECK (empresa_id = nucleo.empresa_actual());

-- Sin DELETE para la aplicación.
GRANT SELECT, INSERT, UPDATE ON pacientes.pacientes TO sg_api;
GRANT SELECT, INSERT ON pacientes.identificadores TO sg_api;
