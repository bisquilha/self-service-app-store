import { useRef, useState } from "react"
import { FileIcon, UploadCloudIcon } from "lucide-react"
import { Button } from "@/components/ui/button"
import { formatBytes } from "@/lib/format"
import { cn } from "@/lib/utils"

type FileDropzoneProps = {
  file: File | null
  onFileChange: (file: File | null) => void
  /** Extensões aceitas, como no `<input>`: ".exe" ou ".exe,.msi". */
  accept?: string
  disabled?: boolean
  /** Nome acessível do campo (lido por leitores de tela). */
  label?: string
  /** Texto de apoio abaixo da chamada. */
  hint?: string
  className?: string
}

function matchesAccept(file: File, accept?: string): boolean {
  if (!accept) return true
  const rules = accept.split(",").map((rule) => rule.trim().toLowerCase()).filter(Boolean)
  return rules.some((rule) => (rule.startsWith(".") ? file.name.toLowerCase().endsWith(rule) : file.type.toLowerCase() === rule))
}

/**
 * Área de arrastar e soltar um arquivo. Substitui o botão cinza do navegador ("Escolher arquivo / Nenhum arquivo escolhido").
 * O `<input type="file">` cobre a área inteira, então clique, teclado (Tab e Enter) e arrastar funcionam sem código extra.
 */
function FileDropzone({ file, onFileChange, accept, disabled, label = "Arquivo", hint, className }: FileDropzoneProps) {
  const inputRef = useRef<HTMLInputElement>(null)
  const [dragging, setDragging] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const extensions = accept?.split(",").map((rule) => rule.trim()).join(" ou ")

  function take(files: FileList | null) {
    const picked = files?.[0]
    if (!picked) return
    if (!matchesAccept(picked, accept)) {
      setError(`Escolha um arquivo ${extensions}.`)
      return
    }
    setError(null)
    onFileChange(picked)
  }

  return (
    <div className={cn("grid gap-1.5", className)}>
      <div
        data-slot="file-dropzone"
        data-dragging={dragging || undefined}
        onDragOver={(event) => {
          if (disabled) return
          event.preventDefault()
          setDragging(true)
        }}
        onDragLeave={() => setDragging(false)}
        onDrop={(event) => {
          event.preventDefault()
          setDragging(false)
          if (!disabled) take(event.dataTransfer.files)
        }}
        className={cn(
          "relative flex min-h-24 flex-col items-center justify-center gap-1 rounded-lg border-2 border-dashed border-input bg-card px-4 py-5 text-center transition-colors",
          "has-[input:focus-visible]:border-ring has-[input:focus-visible]:ring-3 has-[input:focus-visible]:ring-ring/50",
          dragging && "border-primary bg-accent",
          disabled && "opacity-60"
        )}
      >
        <input
          ref={inputRef}
          type="file"
          accept={accept}
          disabled={disabled}
          aria-label={label}
          className="absolute inset-0 size-full cursor-pointer opacity-0 disabled:cursor-not-allowed"
          onChange={(event) => {
            take(event.target.files)
            event.target.value = ""
          }}
        />
        {file ? (
          <>
            <FileIcon className="size-6 text-muted-foreground" aria-hidden="true" />
            <p className="max-w-full truncate font-medium">{file.name}</p>
            <p className="text-xs text-muted-foreground">{formatBytes(file.size)}</p>
            <div className="relative z-10 mt-1 flex gap-2">
              <Button type="button" variant="secondary" size="sm" disabled={disabled} onClick={() => inputRef.current?.click()}>
                Trocar
              </Button>
              <Button type="button" variant="ghost" size="sm" disabled={disabled} onClick={() => onFileChange(null)}>
                Remover
              </Button>
            </div>
          </>
        ) : (
          <>
            <UploadCloudIcon className="size-6 text-muted-foreground" aria-hidden="true" />
            <p className="font-medium">Arraste o arquivo aqui ou clique para escolher</p>
            {hint ? <p className="text-xs text-muted-foreground">{hint}</p> : null}
          </>
        )}
      </div>
      {error ? (
        <p role="alert" className="text-xs text-destructive">
          {error}
        </p>
      ) : null}
    </div>
  )
}

export { FileDropzone }
