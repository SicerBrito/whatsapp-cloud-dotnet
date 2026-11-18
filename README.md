# WhatsApp Cloud API con .NET 8

Una API pequeña que envía plantillas de WhatsApp con la API oficial de Meta y lleva el estado
real de cada mensaje: en fila, aceptado, enviado, entregado, leído o fallido.

La escribí desde cero para mostrar cómo resuelvo lo que falla en una integración de WhatsApp
cuando ya está en producción, que casi nunca es "mandar el mensaje". **Corre completa sin cuenta
de Meta**: sin token de acceso usa un cliente simulado.

## Lo que resuelve

### Que Meta acepte un mensaje no significa que llegue

| Problema en producción | Cómo lo resuelve |
|---|---|
| Pasado cierto volumen diario, Meta acepta el envío sin error y no lo entrega | Un tope propio de personas distintas por 24 horas. Lo que pasa del tope espera en fila y sale cuando se libera un cupo |
| Con el pago de la cuenta vencido o el token caducado, Meta acepta los mensajes y avisa del fallo minutos después. Si nadie frena, se gasta la lista entera | **Freno**: un rechazo de la cuenta detiene la fila al instante; si los fallos de la cuenta llegan por el webhook, la detiene al quinto en una hora. Al reanudar, lo que falló por la cuenta vuelve a la fila |
| Meta aceptó los mensajes y nunca más dijo nada | `/estado-envios` avisa cuando hay mensajes aceptados sin noticias por más de 15 minutos. Casi siempre es que los avisos no llegan: la cuenta no está suscrita a la app (`subscribed_apps`) |

### No todos los errores se tratan igual

Cada error de Meta se traduce a un código estable (`problema_de_pago`, `plantilla_no_existe`…)
y a una **clase**, que dice de quién es la culpa y qué hacer:

| Clase | Ejemplos | Qué hace |
|---|---|---|
| Persona | El número no tiene WhatsApp (131026), pidió no recibir (131050) | Queda fallido; las demás personas siguen |
| Mensaje | Variables que no coinciden (132000), plantilla inexistente (132001) | Queda fallido; lo corrige quien lo pidió |
| Cuenta | Pago (131042), token (190), cuenta bloqueada (131031), plantilla pausada (132015) | Detiene la fila: le va a pasar igual al siguiente |
| Temporal | Límite de velocidad (130429), Meta caída (131000), sin conexión | Reintenta más tarde con espera creciente, sin frenar a los demás |

### Lo que se rompe con datos reales

| Problema en producción | Cómo lo resuelve |
|---|---|
| Cualquiera que conozca la URL del webhook puede marcar mensajes como entregados | Cada llamada se valida con la firma `X-Hub-Signature-256` (HMAC-SHA256 con el secreto de la app), comparada en tiempo constante |
| Los avisos llegan repetidos y fuera de orden ("leído" antes que "entregado") | Los estados solo avanzan en una escala; "fallido" es final; los repetidos se ignoran. Un aviso atrasado de un intento anterior no mueve el reintento |
| Un aviso con un formato nuevo hace que el webhook responda error, Meta lo reintenta en bucle y termina desactivándolo | Con la firma válida se responde 200 siempre, aunque el contenido no se entienda |
| El sistema que llama reintenta porque no recibió respuesta y la persona recibe el mismo WhatsApp dos veces | `referencia`: con la misma, se devuelve el mensaje que ya existe y no se envía otra vez |
| Un nombre o una dirección digitados a mano con un salto de línea hacen que Meta rechace el envío entero | Las variables se limpian antes de enviar (saltos de línea, tabulaciones, espacios seguidos) |
| Probar contra una copia de la base real le escribe a clientes de verdad, y un WhatsApp enviado no se recoge | `NumeroDesvioPruebas`: si tiene un número, todo sale hacia él, sin importar a quién iba |
| No se sabe cuánto cuesta cada mensaje | Se guarda el cobro que Meta informa en cada aviso (categoría y tipo), aunque el aviso llegue tarde |
| La persona toca "Confirmo" en la plantilla y nadie sabe a qué mensaje respondía | Las respuestas de botones y listas quedan atadas al mensaje que las originó |

## Correrlo

```bash
dotnet run --project src/WhatsAppCloud.Api
```

En desarrollo arranca en modo simulado, con un tope de 3 personas para ver la fila en acción.

```bash
# Encolar una plantilla (la referencia evita duplicados si se reintenta)
curl -X POST http://localhost:5213/mensajes/plantilla \
  -H "Content-Type: application/json" \
  -d '{"destinatario":"+57 300 000 0001","plantilla":"recordatorio_cita","idioma":"es_CO",
       "parametros":["Ana"],"botonUrl":"c/8841","referencia":"cita-8841"}'

# Ver su estado, intentos, cobro e historial
curl http://localhost:5213/mensajes/{id}

# Tope, fila, freno y alertas
curl http://localhost:5213/estado-envios

# Detener todo a mano / reanudar (vuelve a la fila lo que falló por la cuenta)
curl -X POST http://localhost:5213/envios/pausar
curl -X POST http://localhost:5213/envios/reanudar
```

Para enviar de verdad, configura `WhatsApp:IdNumeroTelefono`, `WhatsApp:TokenAcceso`,
`WhatsApp:SecretoApp` y `WhatsApp:TokenVerificacion` (con *user secrets* o variables de
entorno, nunca en el repositorio), registra `https://tu-dominio/webhook` en la app de Meta y
suscribe la cuenta de WhatsApp Business a la app. Para el primer envío real, pon tu propio
número en `WhatsApp:NumeroDesvioPruebas`.

| Ajuste | Por omisión | Para qué |
|---|---|---|
| `TopeDestinatariosPorDia` | 250 | Personas distintas en 24 horas, según el límite de la cuenta en Meta |
| `MaximoIntentos` | 3 | Intentos por mensaje ante fallos temporales |
| `EsperaPrimerReintento` | 30 s | Se duplica en cada intento |
| `FallosDeCuentaParaPausar` | 5 | Fallos de la cuenta en una hora, avisados por el webhook, para detener la fila |
| `NumeroDesvioPruebas` | vacío | Si tiene un número, todo sale hacia él |

## Endpoints

| Método | Ruta | Para qué |
|---|---|---|
| `POST` | `/mensajes/plantilla` | Encola una plantilla. `202` con el id; `200` si la referencia ya existía; `409` si la referencia se usó con otro número |
| `GET` | `/mensajes/{id}` | Estado, id de Meta, intentos, error traducido, cobro e historial |
| `GET` | `/mensajes/recibidos` | Lo que escribieron las personas, atado al mensaje al que respondían |
| `GET` | `/estado-envios` | Tope, fila, freno, desvío y alerta de mensajes sin avisos |
| `POST` | `/envios/pausar` | Detiene la fila |
| `POST` | `/envios/reanudar` | La reanuda y vuelve a poner en fila lo que falló por la cuenta o por el momento |
| `GET` | `/webhook` | Verificación de Meta al suscribir el webhook |
| `POST` | `/webhook` | Avisos de Meta, con la firma validada |

## Estructura

```
src/WhatsAppCloud.Api
├── Configuracion/   Opciones de la app de Meta, límites, reintentos y desvío
├── Meta/            Cliente de la Graph API, firma del webhook, modelos y clasificación de errores
├── Envios/          Registro de mensajes, tope de 24 horas, freno, fila y procesador en segundo plano
└── Endpoints/       Webhook, mensajes y control de la fila (minimal APIs)
tests/WhatsAppCloud.Tests   62 pruebas: unitarias y de integración con WebApplicationFactory
```

```bash
dotnet test
```

## Lo que falta para producción

- El registro y la fila viven en memoria: en producción van a la base de datos, para no perder
  mensajes si el servicio se reinicia.
- Horario de envío: no mandar recordatorios a las 2 de la mañana.
- Mensajes de texto libre dentro de la ventana de 24 horas que abre la respuesta de la persona.
- Autenticación de los endpoints de mensajes (aquí solo el webhook está protegido, con su firma).

---

Hecho por [Sicer Brito](https://sicerbrito.dev). Integro facturación electrónica DIAN, pagos,
WhatsApp e IA: [sicerbrito.dev/servicios](https://sicerbrito.dev/servicios).
