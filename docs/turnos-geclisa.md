# Turnos: correspondencia con Geclisa

Fuente: `script.sql`, export del esquema de Geclisa del 20/10/2026 (tablas `Turnos`, `Cronograma`, `CronogramaxOS`,
`Feriados`, `TurnosBorrados` y procedimientos `panet_Turnos_*`). No se revisaron datos de producción.

## Cómo lo hace Geclisa hoy

| Tema | Geclisa | Riesgo |
| --- | --- | --- |
| Doble turno | `panet_Turnos_VerificarSiYaEstaReservado` hace un `SELECT COUNT` y después `panet_Turnos_Insertar` inserta. No hay índice único ni restricción en `Turnos`. | Dos puestos que reservan a la vez pueden dar el mismo turno: el control y la inserción no son atómicos. |
| Alcance del control | Compara servicio, fecha, `Hs_Ini` exacta y prestador. | No detecta turnos superpuestos de distinta duración, ni el mismo profesional en dos servicios a la misma hora. |
| Cancelación | `panet_Turnos_Eliminar` hace `DELETE`; `TurnosBorrados` guarda una copia aparte. | El turno desaparece de la tabla principal; la trazabilidad depende de que la aplicación copie antes de borrar. |
| Datos del paciente | `Turnos` copia documento, apellido, nombre, sexo, edad, teléfono y email. | Los datos quedan desactualizados respecto de `Ficha`. |
| Horas | `Hs_Ini` y `Hs_Fin` son `smallint` con formato HHMM (por ejemplo 800 y 1830). | Sin zona horaria; aritmética manual. |
| Auditoría | `Usu_Alta` y `Usu_Modi` como texto libre. | Sin vínculo a un usuario real. |

## Cómo lo hace el sistema nuevo

| Tema | Sistema nuevo |
| --- | --- |
| Doble turno | Restricción de exclusión `ex_turnos_profesional_cupo`: un profesional no puede tener dos turnos activos superpuestos en el mismo cupo. Los turnos de un profesional se dan de a uno con un bloqueo consultivo. Probado con 10 pedidos simultáneos. |
| Alcance | Por profesional, sin importar servicio ni consultorio. Además, `ex_turnos_paciente` impide que un paciente tenga dos turnos superpuestos. |
| Cancelación | Cambia el estado a `CANCELADO` con motivo, fecha y usuario. Nunca se borra. |
| Datos del paciente | Se referencia `pacientes.pacientes`; no se copian. |
| Horas | `tstzrange` con zona horaria; la agenda se define en hora local de la empresa. |
| Auditoría | `auditoria.eventos`, vinculada a `nucleo.usuarios`. |

## Correspondencia de campos

| Geclisa | Sistema nuevo | Nota |
| --- | --- | --- |
| `Turnos.turno_id` | `turnos.turnos.geclisa_id` | El id propio es un UUID. |
| `serv_id` | `servicio_id` → `catalogos.servicios.geclisa_id` | |
| `pre_id` | `profesional_id` → `catalogos.profesionales.geclisa_id` | |
| `Cons_id` | `consultorio_id` → `catalogos.consultorios.geclisa_id` | |
| `ficha_id` | `paciente_id` → identificador `GECLISA_FICHA` del paciente | |
| `tur_fecha` + `Hs_Ini` / `Hs_Fin` | `horario` (`tstzrange`) | HHMM convertido a hora local de la empresa. |
| `os_id`, `plan_id`, `Nro_Afiliado` | `cobertura_id`, `plan_id`, `numero_afiliado` | |
| `tur_Obs` | `observaciones` | |
| `esWeb`, `tOrigen_id` | `origen` | `TurnosOrigen` se mapea a PRESENCIAL, TELEFONO, WEB o API. Falta ver sus valores reales. |
| `confirmado`, `tur_FechaEspera`, `tur_FechaAtendido` | `estado` | CONFIRMADO, PRESENTE, ATENDIDO. |
| `Cro_id` | `agenda_id` → `turnos.agendas.geclisa_id` | |
| `nom_id`, `nom_cod`, `nom_nom` | (pendiente) | La práctica del nomenclador se agrega con facturación. |
| `tfic_*`, `tsexo`, `tedad` | (no se copia) | Están en el paciente. |
| `Cronograma.dia_id`, `Hs_Ini`, `Hs_Fin`, `Fec_Ini`, `Fec_Fin` | `dia_semana`, `hora_desde`, `hora_hasta`, `vigente_desde`, `vigente_hasta` | Falta confirmar si `dia_id` 1 es domingo o lunes (tabla `Dias`). |
| `Cronograma.turt_id` → `TurnosTiempo.turt_tiempo` | `duracion_minutos` | |
| `Cronograma.CantPacientesSimultaneos` | `capacidad` | |
| `Cronograma.CantMaxEntreTur` | `sobreturnos` | En Geclisa son "entre turnos"; acá son cupos extra del mismo horario. Confirmar con admisión. |
| `CronogramaxOS` | `turnos.agendas_coberturas` | |
| `Feriados`, `Feriados_PrestadoresExcluidos` | `turnos.bloqueos` | Un feriado es un bloqueo sin profesional. Las excepciones por prestador quedan pendientes. |

## Convivencia

Cada agenda tiene un dueño (`gestionada_por`): `GECLISA` o `SISTEMA_NUEVO`. El sistema nuevo no da turnos en agendas
de Geclisa y lo rechaza con `409`. Para migrar una agenda:

1. Importar la agenda con `gestionadaPor = GECLISA` y sus turnos futuros (puente, pendiente).
2. Dejar de dar turnos para ese profesional en Geclisa.
3. `PUT /api/v1/agendas/{id}/gestion` con `SISTEMA_NUEVO`. Se publica el evento `turnos.agenda_gestion_cambiada`.

El paso 2 depende de que Geclisa permita bloquear la carga por prestador; hay que confirmarlo con el proveedor o con el uso diario.
