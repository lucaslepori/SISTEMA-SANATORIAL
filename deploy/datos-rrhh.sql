-- Datos de ejemplo de RRHH para desarrollo local. No usar en producción.
--
-- Personas ficticias: los nombres son inventados y los CUIL usan DNI de la serie 99.000.000,
-- que no está asignada, con dígito verificador válido para que pasen la validación.
-- Nunca cargar acá datos reales de personal (Ley 25.326).

INSERT INTO nucleo.usuarios (id, sujeto_oidc, nombre_mostrar) VALUES
    ('0192f000-0000-7000-8000-0000000000a4', 'rrhh.local', 'Recursos humanos (local)')
ON CONFLICT DO NOTHING;
INSERT INTO nucleo.usuarios_empresas (usuario_id, empresa_id, roles) VALUES
    ('0192f000-0000-7000-8000-0000000000a4', '0192f000-0000-7000-8000-000000000001', '{rrhh}')
ON CONFLICT DO NOTHING;

INSERT INTO rrhh.sectores (id, empresa_id, nombre) VALUES
    ('0192f000-0000-7000-8000-0000000000d1', '0192f000-0000-7000-8000-000000000001', 'Enfermería'),
    ('0192f000-0000-7000-8000-0000000000d2', '0192f000-0000-7000-8000-000000000001', 'Administración'),
    ('0192f000-0000-7000-8000-0000000000d3', '0192f000-0000-7000-8000-000000000001', 'Consultorios externos')
ON CONFLICT DO NOTHING;

INSERT INTO rrhh.puestos (id, empresa_id, nombre) VALUES
    ('0192f000-0000-7000-8000-0000000000d4', '0192f000-0000-7000-8000-000000000001', 'Enfermero/a profesional'),
    ('0192f000-0000-7000-8000-0000000000d5', '0192f000-0000-7000-8000-000000000001', 'Administrativo/a'),
    ('0192f000-0000-7000-8000-0000000000d6', '0192f000-0000-7000-8000-000000000001', 'Médico/a de planta')
ON CONFLICT DO NOTHING;

-- El legajo 4 es la profesional de catálogos "Dra. Local": se enlaza, no se duplica.
INSERT INTO rrhh.empleados
    (id, empresa_id, legajo, apellidos, nombres, cuil, fecha_nacimiento, sexo_registral, modalidad, convenio, categoria,
     horas_semanales, sector_id, puesto_id, fecha_ingreso, profesional_id, usuario_id, creado_por)
VALUES
    ('0192f000-0000-7000-8000-0000000000e1', '0192f000-0000-7000-8000-000000000001', 1, 'Ficticio', 'Persona Uno',
     '20990000019', '1985-03-10', 'M', 'RELACION_DEPENDENCIA', 'CCT 122/75', 'Enfermería profesional', 36,
     '0192f000-0000-7000-8000-0000000000d1', '0192f000-0000-7000-8000-0000000000d4', '2015-06-01', NULL, NULL,
     '0192f000-0000-7000-8000-0000000000a3'),
    ('0192f000-0000-7000-8000-0000000000e2', '0192f000-0000-7000-8000-000000000001', 2, 'Ficticia', 'Persona Dos',
     '27990000021', '1992-11-25', 'F', 'RELACION_DEPENDENCIA', 'CCT 122/75', 'Enfermería profesional', 36,
     '0192f000-0000-7000-8000-0000000000d1', '0192f000-0000-7000-8000-0000000000d4', '2021-02-15', NULL, NULL,
     '0192f000-0000-7000-8000-0000000000a3'),
    ('0192f000-0000-7000-8000-0000000000e3', '0192f000-0000-7000-8000-000000000001', 3, 'Ejemplo', 'Persona Tres',
     '20990000043', '1978-07-02', 'X', 'RELACION_DEPENDENCIA', 'CCT 122/75', 'Administrativo', 44,
     '0192f000-0000-7000-8000-0000000000d2', '0192f000-0000-7000-8000-0000000000d5', '2010-09-01', NULL, NULL,
     '0192f000-0000-7000-8000-0000000000a3'),
    ('0192f000-0000-7000-8000-0000000000e4', '0192f000-0000-7000-8000-000000000001', 4, 'Local', 'Dra.',
     '27990000056', '1980-01-20', 'F', 'MONOTRIBUTO', NULL, NULL, 20,
     '0192f000-0000-7000-8000-0000000000d3', '0192f000-0000-7000-8000-0000000000d6', '2018-04-01',
     '0192f000-0000-7000-8000-0000000000b6', '0192f000-0000-7000-8000-0000000000a2',
     '0192f000-0000-7000-8000-0000000000a3')
ON CONFLICT DO NOTHING;

-- Una licencia pedida y una aprobada, con fechas relativas a la primera carga.
INSERT INTO rrhh.licencias (id, empresa_id, empleado_id, tipo, desde, hasta, estado, resuelta_en, resuelta_por, creado_por) VALUES
    ('0192f000-0000-7000-8000-0000000000f1', '0192f000-0000-7000-8000-000000000001', '0192f000-0000-7000-8000-0000000000e1',
     'VACACIONES', current_date + 30, current_date + 43, 'SOLICITADA', NULL, NULL, '0192f000-0000-7000-8000-0000000000a3'),
    ('0192f000-0000-7000-8000-0000000000f2', '0192f000-0000-7000-8000-000000000001', '0192f000-0000-7000-8000-0000000000e4',
     'EXAMEN', current_date + 10, current_date + 11, 'APROBADA', now(), '0192f000-0000-7000-8000-0000000000a3',
     '0192f000-0000-7000-8000-0000000000a3')
ON CONFLICT DO NOTHING;

-- Documentación: un seguro por vencer, un apto médico vencido y una matrícula vigente.
INSERT INTO rrhh.documentos (id, empresa_id, empleado_id, tipo, descripcion, numero, emitido, vence, creado_por) VALUES
    ('0192f000-0000-7000-8000-0000000000f3', '0192f000-0000-7000-8000-000000000001', '0192f000-0000-7000-8000-0000000000e4',
     'SEGURO_MALA_PRAXIS', 'Póliza de responsabilidad profesional', 'POL-0001', current_date - 345, current_date + 20,
     '0192f000-0000-7000-8000-0000000000a3'),
    ('0192f000-0000-7000-8000-0000000000f4', '0192f000-0000-7000-8000-000000000001', '0192f000-0000-7000-8000-0000000000e2',
     'APTO_MEDICO', 'Examen periódico', NULL, current_date - 400, current_date - 35, '0192f000-0000-7000-8000-0000000000a3'),
    ('0192f000-0000-7000-8000-0000000000f5', '0192f000-0000-7000-8000-000000000001', '0192f000-0000-7000-8000-0000000000e1',
     'MATRICULA', 'Matrícula provincial de enfermería', 'ENF-1234', '2015-05-01', current_date + 400,
     '0192f000-0000-7000-8000-0000000000a3')
ON CONFLICT DO NOTHING;
