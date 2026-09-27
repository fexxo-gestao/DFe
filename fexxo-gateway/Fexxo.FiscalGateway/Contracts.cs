namespace Fexxo.FiscalGateway;

public sealed record CertificadoDto(string PfxBase64, string Senha);

public sealed record InspecionarCertificadoRequest(CertificadoDto Certificado);

public sealed record InspecionarCertificadoResponse(string? Cnpj, string Titular, DateTimeOffset ValidoDe, DateTimeOffset ValidoAte);

public sealed record NfseNacionalRequest(string Ambiente, CertificadoDto Certificado, string DpsXml);

public sealed record AssinarNfseResponse(string DpsAssinadaXml);

public sealed record ErroFiscalDto(string Codigo, string Mensagem);

public static class StatusEmissao
{
    public const string Autorizada = "authorized";
    public const string Rejeitada = "rejected";
}

public sealed record EmitirNfseResponse(
    string Status,
    string DpsAssinadaXml,
    string? ChaveAcesso,
    string? Numero,
    string? NfseXml,
    IReadOnlyList<ErroFiscalDto> Erros);

public sealed record ProblemaDto(string Codigo, string Mensagem);

public sealed record ConsultarDpsRequest(string Ambiente, CertificadoDto Certificado, string IdDps);

public sealed record ConsultarDpsResponse(bool Encontrada, string? ChaveAcesso, string? Numero, string? NfseXml, IReadOnlyList<ErroFiscalDto> Erros);

public static class StatusEvento
{
    public const string Registrado = "registered";
    public const string Rejeitado = "rejected";
}

public sealed record RegistrarEventoRequest(string Ambiente, CertificadoDto Certificado, string PedidoXml);

public sealed record RegistrarEventoResponse(string Status, string PedidoAssinadoXml, string? EventoXml, IReadOnlyList<ErroFiscalDto> Erros);

public sealed record CscDto(int Id, string Valor);

public static class StatusNfce
{
    public const string Autorizada = "authorized";
    public const string Rejeitada = "rejected";
    public const string Duplicada = "duplicate";
    public const string ContingenciaAssinada = "contingency_signed";
}

public static class SituacaoNfce
{
    public const string Autorizada = "authorized";
    public const string Cancelada = "cancelled";
    public const string Denegada = "denied";
    public const string NaoEncontrada = "not_found";
    public const string Desconhecida = "unknown";
}

public sealed record AutorizarNfceRequest(string Ambiente, CertificadoDto Certificado, CscDto? Csc, string NfeXml, bool Contingencia);

public sealed record TransmitirContingenciaNfceRequest(string Ambiente, CertificadoDto Certificado, CscDto? Csc, string NfeAssinadaXml);

public sealed record AutorizarNfceResponse(
    string Status,
    string NfeAssinadaXml,
    string? ChaveAcesso,
    string? Protocolo,
    string? NfeProcXml,
    string? QrCodeUrl,
    IReadOnlyList<ErroFiscalDto> Erros);

public sealed record ConsultarNfceRequest(string Ambiente, CertificadoDto Certificado, string Chave);

public sealed record ConsultarNfceResponse(string Situacao, string? Protocolo, string? ProtNFeXml, IReadOnlyList<ErroFiscalDto> Erros);

public sealed record StatusNfceRequest(string Ambiente, CertificadoDto Certificado, string Uf);

public sealed record StatusNfceResponse(bool EmOperacao, string CStat, string Motivo);

public sealed record PedidoNfceRequest(string Ambiente, CertificadoDto Certificado, string PedidoXml);

public sealed record InutilizarNfceResponse(string Status, string? Protocolo, string? ProcInutilizacaoXml, IReadOnlyList<ErroFiscalDto> Erros);
