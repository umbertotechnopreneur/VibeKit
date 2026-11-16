# ramtaskman

[Italiano](README.it.md) · [English](README.en.md) · [中文（简体）](README.zh.md) · [한국어](README.ko.md) · [Français](README.fr.md) · [Tiếng Việt](README.vi.md) · [Español](README.es.md) · [Português](README.pt.md) · [Deutsch](README.de.md) · [日本語](README.ja.md)

### Descubre adónde va la RAM, directamente desde la terminal.

ramtaskman es un monitor experimental de memoria para Linux y Windows. Nació de la curiosidad y la investigación para entender la memoria en distintos sistemas sin recurrir a herramientas complicadas. Es un proyecto Vibeware.

![ramtaskman](../images/overview.png)

*Interfaz Textual real capturada en Linux con datos sintéticos de demostración. Las capturas muestran la interfaz italiana actual.*


![Multilingual dashboard](../images/multilingual.png)

*Las capturas muestran una etapa anterior de traducción. La aplicación ahora ofrece diez idiomas.*

## Funciones principales

1. Consulta RAM, memoria disponible, caché e historial de un vistazo; elige el modo Slim o diagnóstico completo.
2. Explora aplicaciones y procesos, ordena las métricas disponibles y comprueba la cobertura.
3. Consulta RSS y USS; en Linux también PSS, swap y SwapPSS, con detalles y datos ausentes indicados.
4. Busca por nombre, ejecutable o PID exacto y filtra por usuario, swap o datos parciales.
5. Explora regiones de memoria en Linux; la vista virtual de Windows es experimental.
6. Compara dos muestras y guarda o abre capturas locales, incluso sin conexión.

## Un vistazo al interior

![Full diagnostics](../images/full-diagnostics.png)

![Process regions](../images/regions-resident.png)

## Cómo empezar

Se necesitan Python 3.11+ y uv. Linux requiere /proc y smaps_rollup. El primer inicio necesita red para instalar las dependencias fijadas. El entorno y la caché permanecen en el proyecto.

```bash
git clone https://github.com/umbertotechnopreneur/VibeKit.git
cd VibeKit/Apps/RamTaskman
./run.sh
```

En Windows, desde la carpeta del proyecto en Windows Terminal:

```powershell
.\run.cmd
```

Usa --once o --json para una muestra única o salida JSON. No compartas .venv entre sistemas: vuelve a crearlo localmente. El proyecto no está publicado en PyPI.

Teclas útiles: m cambia de modo, g alterna aplicaciones/procesos, / busca, f filtra, d muestra detalles, r abre regiones, ? explica las métricas y q sale. La guía técnica completa está actualmente en italiano.

`./run.sh --language es` · Windows: `.\run.cmd --language es`

La interfaz, el pie, la ayuda, los mensajes principales de la CLI y los diagnósticos de la aplicación están disponibles en diez idiomas. Las nuevas opciones de valor de RAM y algunos textos de Acerca de siguen en inglés.

[RAM valuation and current validation status (English)](../../README.md)

## Experimental: interpreta los datos con cuidado

Las lecturas son secuenciales, los permisos pueden limitar la visibilidad y la agrupación de aplicaciones es heurística. Los datos ausentes son n/a, no cero. RSS puede contar páginas compartidas varias veces; la PSS de procesos no es toda la RAM. La caché se solapa con la memoria disponible. La swap por proceso no se deduce del commit.

Linux ofrece PSS y swap por proceso cuando son legibles; aquí Windows no los expone. Linux usa smaps y Windows muestra intervalos virtuales experimentales. La verificación local se realizó en Linux; Windows necesita validación. macOS no está soportado porque no tengo un Mac para desarrollarlo y probarlo. Son bienvenidas las contribuciones para mantenerlo.

## Inspiración y agradecimientos

Gracias a Scott Hanselman y a GPU Taskman por la inspiración. ramtaskman reutiliza partes del proyecto y adapta su interfaz de terminal a la RAM; es independiente y no es una versión oficial. [Scott Hanselman](https://github.com/shanselman) · [GPU Taskman](https://github.com/shanselman/gputaskman) · [Provenance](../upstream.md)

Traducciones realizadas con ayuda de IA. Se agradecen las correcciones de traductores y hablantes nativos.

[Guía técnica completa (italiano)](../guida.md) · [Licencia MIT; se conserva el copyright original.](../../LICENSE)
