using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace Fexxo.FiscalGateway;

public sealed class CertificadoInvalidoException(string mensagem) : Exception(mensagem);

public static partial class Certificados
{
    private const string OidCnpjIcpBrasil = "2.16.76.1.3.3";
    private const string OidSubjectAlternativeName = "2.5.29.17";

    public static X509Certificate2 Carregar(CertificadoDto dto)
    {
        byte[] pfx;
        try
        {
            pfx = Convert.FromBase64String(dto.PfxBase64);
        }
        catch (FormatException)
        {
            throw new CertificadoInvalidoException("O arquivo do certificado não está em base64.");
        }

        try
        {
            var certificado = new X509Certificate2(pfx, dto.Senha, X509KeyStorageFlags.Exportable);
            if (!certificado.HasPrivateKey)
            {
                certificado.Dispose();
                throw new CertificadoInvalidoException("O certificado não contém a chave privada.");
            }
            return certificado;
        }
        catch (CryptographicException)
        {
            throw new CertificadoInvalidoException("Não foi possível abrir o certificado. Confira o arquivo .pfx e a senha.");
        }
    }

    public static InspecionarCertificadoResponse Inspecionar(X509Certificate2 certificado) =>
        new(
            CnpjDoCertificado(certificado),
            certificado.GetNameInfo(X509NameType.SimpleName, false),
            new DateTimeOffset(certificado.NotBefore.ToUniversalTime(), TimeSpan.Zero),
            new DateTimeOffset(certificado.NotAfter.ToUniversalTime(), TimeSpan.Zero));

    public static string? CnpjDoCertificado(X509Certificate2 certificado)
    {
        var doSan = CnpjDaExtensaoIcpBrasil(certificado);
        if (doSan is not null)
        {
            return doSan;
        }

        var nomeComum = certificado.GetNameInfo(X509NameType.SimpleName, false);
        var sufixo = CnpjNoFimDoNome().Match(nomeComum);
        return sufixo.Success ? sufixo.Groups[1].Value : null;
    }

    private static string? CnpjDaExtensaoIcpBrasil(X509Certificate2 certificado)
    {
        var san = certificado.Extensions.Cast<X509Extension>().FirstOrDefault(e => e.Oid?.Value == OidSubjectAlternativeName);
        if (san is null)
        {
            return null;
        }

        var bruto = System.Text.Encoding.ASCII.GetString(san.RawData);
        var oidCodificado = System.Text.Encoding.ASCII.GetString(CodificarOid(OidCnpjIcpBrasil));
        var posicao = bruto.IndexOf(oidCodificado, StringComparison.Ordinal);
        if (posicao < 0)
        {
            return null;
        }

        var depois = bruto[(posicao + oidCodificado.Length)..];
        var cnpj = QuatorzeDigitos().Match(depois);
        return cnpj.Success ? cnpj.Value : null;
    }

    private static byte[] CodificarOid(string oid) => new System.Formats.Asn1.AsnWriter(System.Formats.Asn1.AsnEncodingRules.DER)
        .Also(w => w.WriteObjectIdentifier(oid))
        .Encode()[2..];

    private static T Also<T>(this T valor, Action<T> acao)
    {
        acao(valor);
        return valor;
    }

    [GeneratedRegex(@":(\d{14})$")]
    private static partial Regex CnpjNoFimDoNome();

    [GeneratedRegex(@"\d{14}")]
    private static partial Regex QuatorzeDigitos();
}
