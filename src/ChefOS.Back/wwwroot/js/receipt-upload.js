// Préparation du fichier d'un ticket avant son envoi au serveur (importé à la demande par ReceiptNew.razor).
//
// Une photo de téléphone dépasse souvent 5 Mo, limite de l'API de lecture : les images sont donc redimensionnées dans le navigateur
// (grand côté plafonné à 2000 px, JPEG qualité 0,85). Les PDF partent tels quels. Si le redimensionnement échoue, on envoie l'original.

const MaxSide = 2000;
const JpegQuality = 0.85;

/**
 * Lit le fichier choisi dans un <input type="file"> et renvoie { name, contentType, length, data }, où data est un flux
 * (IJSStreamReference côté .NET) : les octets ne passent pas par un message JSON. Renvoie null s'il n'y a pas de fichier.
 */
export async function prepare(input) {
    const file = input?.files?.[0];
    if (!file) {
        return null;
    }

    let blob = file;
    let name = file.name;
    let contentType = file.type;

    if (file.type.startsWith('image/')) {
        try {
            const resized = await resizeToJpeg(file);
            if (resized) {
                blob = resized;
                name = withExtension(file.name, 'jpg');
                contentType = 'image/jpeg';
            }
        } catch (error) {
            console.warn('Redimensionnement impossible, envoi du fichier d\'origine.', error);
        }
    }

    return { name, contentType, length: blob.size, data: DotNet.createJSStreamReference(blob) };
}

/** Vide le champ : sans cela, choisir une seconde fois le même fichier ne déclencherait pas d'évènement « change ». */
export function reset(input) {
    if (input) {
        input.value = '';
    }
}

async function resizeToJpeg(file) {
    const image = await decode(file);
    try {
        const scale = Math.min(1, MaxSide / Math.max(image.width, image.height));
        const width = Math.max(1, Math.round(image.width * scale));
        const height = Math.max(1, Math.round(image.height * scale));

        const canvas = document.createElement('canvas');
        canvas.width = width;
        canvas.height = height;

        const context = canvas.getContext('2d');
        if (!context) {
            return null;
        }

        // Fond blanc : le JPEG n'a pas de transparence (un PNG transparent deviendrait noir).
        context.fillStyle = '#ffffff';
        context.fillRect(0, 0, width, height);
        context.drawImage(image.source, 0, 0, width, height);

        const blob = await new Promise(resolve => canvas.toBlob(resolve, 'image/jpeg', JpegQuality));
        return blob && blob.size > 0 ? blob : null;
    } finally {
        image.release();
    }
}

// L'orientation EXIF des photos de téléphone est appliquée au décodage : le JPEG produit est à l'endroit.
async function decode(file) {
    if ('createImageBitmap' in window) {
        try {
            const bitmap = await createImageBitmap(file, { imageOrientation: 'from-image' });
            return { source: bitmap, width: bitmap.width, height: bitmap.height, release: () => bitmap.close() };
        } catch {
            // Navigateur sans cette option ou format non décodé ainsi : on essaie avec un élément <img>.
        }
    }

    const url = URL.createObjectURL(file);
    const element = new Image();
    element.src = url;
    try {
        await element.decode();
    } catch (error) {
        URL.revokeObjectURL(url);
        throw error;
    }

    return {
        source: element,
        width: element.naturalWidth,
        height: element.naturalHeight,
        release: () => URL.revokeObjectURL(url)
    };
}

function withExtension(fileName, extension) {
    const dot = fileName.lastIndexOf('.');
    const stem = dot > 0 ? fileName.slice(0, dot) : fileName;
    return `${stem || 'ticket'}.${extension}`;
}
