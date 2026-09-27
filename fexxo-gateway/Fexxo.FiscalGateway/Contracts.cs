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
