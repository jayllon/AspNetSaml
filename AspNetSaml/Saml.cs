/*	Jitbit's simple SAML 2.0 component for ASP.NET
	https://github.com/jitbit/AspNetSaml/
	(c) Jitbit LP, 2016-2025
	Use this freely under the Apache license (see https://choosealicense.com/licenses/apache-2.0/)
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.IO.Compression;
using System.Text;
using System.Runtime;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;


namespace Saml
{
	public abstract class BaseSamlMessage
	{
		protected XmlDocument _xmlDoc;
		protected readonly X509Certificate2 _certificate;
		protected XmlNamespaceManager _xmlNameSpaceManager; //we need this one to run our XPath queries on the SAML XML

		public string Xml { get { return _xmlDoc.OuterXml; } }

		public BaseSamlMessage(string certificateStr, string responseString = null) : this(Encoding.ASCII.GetBytes(EnsureCertFormat(certificateStr)), responseString) { }

		public BaseSamlMessage(byte[] certificateBytes, string responseString = null)
		{
			_certificate = new X509Certificate2(certificateBytes);
			if (responseString != null)
				LoadXmlFromBase64(responseString);
		}

		/// <summary>
		/// Parse SAML response XML (in case was it not passed in constructor)
		/// </summary>
		public void LoadXml(string xml)
		{
			_xmlDoc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
			_xmlDoc.LoadXml(xml);

			_xmlNameSpaceManager = GetNamespaceManager(); //lets construct a "manager" for XPath queries
		}

		//linux fix (not working when there's no "-----BEGIN CERTIFICATE-----xxxx-----END CERTIFICATE-----"
		//also remove double line breaks
		private static string EnsureCertFormat(string cert)
		{
			var samlCertificate = cert.Replace("\r", "").Replace("\n\n", "\n");
			if (!samlCertificate.StartsWith("-----BEGIN CERTIFICATE-----"))
			{
				samlCertificate = "-----BEGIN CERTIFICATE-----\n" + samlCertificate.Trim();
			}
			if (!samlCertificate.EndsWith("-----END CERTIFICATE-----"))
			{
				samlCertificate = samlCertificate.Trim() + "\n-----END CERTIFICATE-----";
			}

			return samlCertificate;
		}

		public void LoadXmlFromBase64(string response)
		{
			UTF8Encoding enc = new UTF8Encoding();
			LoadXml(enc.GetString(Convert.FromBase64String(response)));
		}

		//an XML signature can "cover" not the whole document, but only a part of it
		//.NET's built in "CheckSignature" does not cover this case, it will validate to true.
		//We should check the signature reference, so it "references" the id of the root document element! If not - it's a hack
		protected bool ValidateSignatureReference(SignedXml signedXml)
		{
			if (signedXml.SignedInfo.References.Count != 1) //no ref at all
				return false;

			var reference = (Reference)signedXml.SignedInfo.References[0];
			var id = reference.Uri.Substring(1);

			var idElement = signedXml.GetIdElement(_xmlDoc, id);

			if (idElement == _xmlDoc.DocumentElement)
				return true;
			else //sometimes its not the "root" doc-element that is being signed, but the "assertion" element
			{
				var assertionNode = _xmlDoc.SelectSingleNode("/samlp:Response/saml:Assertion", _xmlNameSpaceManager) as XmlElement;
				if (assertionNode != idElement)
					return false;
			}

			return true;
		}

        protected string SignAuthnRequest(string samlRequestXml, X509Certificate2 certificate)
        {
            XmlDocument xmlDoc = new XmlDocument();
            xmlDoc.PreserveWhitespace = true;
            xmlDoc.LoadXml(samlRequestXml);

            SignedXml signedXml = new SignedXml(xmlDoc);
            signedXml.SigningKey = certificate.PrivateKey;

            Reference reference = new Reference();
            reference.Uri = "#" + xmlDoc.DocumentElement.GetAttribute("ID");

            XmlDsigEnvelopedSignatureTransform env = new XmlDsigEnvelopedSignatureTransform();
            reference.AddTransform(env);

            signedXml.AddReference(reference);

            KeyInfo keyInfo = new KeyInfo();
            keyInfo.AddClause(new KeyInfoX509Data(certificate));
            signedXml.KeyInfo = keyInfo;

            signedXml.ComputeSignature();

            XmlElement xmlDigitalSignature = signedXml.GetXml();
            xmlDoc.DocumentElement.InsertBefore(xmlDigitalSignature, xmlDoc.DocumentElement.FirstChild);

            return xmlDoc.OuterXml;
        }





        //returns namespace manager, we need one b/c MS says so... Otherwise XPath doesnt work in an XML doc with namespaces
        //see https://stackoverflow.com/questions/7178111/why-is-xmlnamespacemanager-necessary
        private XmlNamespaceManager GetNamespaceManager()
		{
			XmlNamespaceManager manager = new XmlNamespaceManager(_xmlDoc.NameTable);
			manager.AddNamespace("ds", SignedXml.XmlDsigNamespaceUrl);
			manager.AddNamespace("saml", "urn:oasis:names:tc:SAML:2.0:assertion");
			manager.AddNamespace("samlp", "urn:oasis:names:tc:SAML:2.0:protocol");

			return manager;
		}

		/// <summary>
		/// Checks the validity of SAML response (validate signature, check expiration date etc)
		/// </summary>
		/// <returns></returns>
		public bool IsValid()
		{
			XmlNodeList nodeList = _xmlDoc.SelectNodes("//ds:Signature", _xmlNameSpaceManager);

			SignedXml signedXml = new SignedXml(_xmlDoc);

			if (nodeList.Count == 0) return false;

			signedXml.LoadXml((XmlElement)nodeList[0]);
			
            return ValidateSignatureReference(signedXml) && signedXml.CheckSignature(_certificate, true) && !IsExpired();

        }

		protected virtual bool IsExpired()
		{
			DateTime expirationDate = DateTime.MaxValue;
			XmlNode node = _xmlDoc.SelectSingleNode("/samlp:Response/saml:Assertion[1]/saml:Subject/saml:SubjectConfirmation/saml:SubjectConfirmationData", _xmlNameSpaceManager);
			if (node != null && node.Attributes["NotOnOrAfter"] != null)
			{
				DateTime.TryParse(node.Attributes["NotOnOrAfter"].Value, out expirationDate);
			}
			return (CurrentTime ?? DateTime.UtcNow) > expirationDate.ToUniversalTime();
		}

		public DateTime? CurrentTime { get; set; } = null; //mostly for unit-testing. STUPID I KNOW, will fix later
	}

	public class Response : BaseSamlMessage
	{
		public Response(string certificateStr, string responseString = null) : base(certificateStr, responseString) { }

		public Response(byte[] certificateBytes, string responseString = null) : base(certificateBytes, responseString) { }

		/// <summary>
		/// returns the User's login
		/// </summary>
		public string GetNameID()
		{
			XmlNode node = _xmlDoc.SelectSingleNode("/samlp:Response/saml:Assertion[1]/saml:Subject/saml:NameID", _xmlNameSpaceManager);
			return node.InnerText;
		}

		public virtual string GetUpn()
		{
			return GetCustomAttribute("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn");
		}

		public virtual string GetEmail()
		{
			return GetCustomAttribute("User.email")
				?? GetCustomAttribute("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress") //some providers (for example Azure AD) put last name into an attribute named "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress"
				?? GetCustomAttribute("mail"); //some providers put last name into an attribute named "mail"
		}

		public virtual string GetFirstName()
		{
			return GetCustomAttribute("first_name")
				?? GetCustomAttribute("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/givenname") //some providers (for example Azure AD) put last name into an attribute named "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/givenname"
				?? GetCustomAttribute("User.FirstName")
				?? GetCustomAttribute("givenName"); //some providers put last name into an attribute named "givenName"
		}

		public virtual string GetLastName()
		{
			return GetCustomAttribute("last_name")
				?? GetCustomAttribute("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/surname") //some providers (for example Azure AD) put last name into an attribute named "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/surname"
				?? GetCustomAttribute("User.LastName")
				?? GetCustomAttribute("sn"); //some providers put last name into an attribute named "sn"
		}

		public virtual string GetDepartment()
		{
			return GetCustomAttribute("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/department")
				?? GetCustomAttribute("department");
		}

		public virtual string GetPhone()
		{
			return GetCustomAttribute("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/homephone")
				?? GetCustomAttribute("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/telephonenumber");
		}

		public virtual string GetCompany()
		{
			return GetCustomAttribute("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/companyname")
				?? GetCustomAttribute("organization")
				?? GetCustomAttribute("User.CompanyName");
		}

		public virtual string GetLocation()
		{
			return GetCustomAttribute("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/location")
				?? GetCustomAttribute("physicalDeliveryOfficeName");
		}

		public string GetCustomAttribute(string attr)
		{
			XmlNode node = _xmlDoc.SelectSingleNode("/samlp:Response/saml:Assertion[1]/saml:AttributeStatement/saml:Attribute[@Name='" + attr + "']/saml:AttributeValue", _xmlNameSpaceManager);
			return node?.InnerText;
		}

		public string GetCustomAttributeViaFriendlyName(string attr)
		{
			XmlNode node = _xmlDoc.SelectSingleNode("/samlp:Response/saml:Assertion[1]/saml:AttributeStatement/saml:Attribute[@FriendlyName='" + attr + "']/saml:AttributeValue", _xmlNameSpaceManager);
			return node?.InnerText;
		}

		public List<string> GetCustomAttributeAsList(string attr)
		{
			XmlNodeList nodes = _xmlDoc.SelectNodes("/samlp:Response/saml:Assertion[1]/saml:AttributeStatement/saml:Attribute[@Name='" + attr + "']/saml:AttributeValue", _xmlNameSpaceManager);
			return nodes?.Cast<XmlNode>().Select(x => x.InnerText).ToList();
		}
	}

	/// <summary>
	/// Represents IdP-generated Logout Response in response to a SP-initiated Logout Request.
	/// </summary>
	public class SignoutResponse : BaseSamlMessage
	{
		public SignoutResponse(string certificateStr, string responseString = null) : base(certificateStr, responseString) { }

		public SignoutResponse(byte[] certificateBytes, string responseString = null) : base(certificateBytes, responseString) { }

		public string GetLogoutStatus()
		{
			XmlNode node = _xmlDoc.SelectSingleNode("/samlp:LogoutResponse/samlp:Status/samlp:StatusCode", _xmlNameSpaceManager);
			return node?.Attributes["Value"].Value.Replace("urn:oasis:names:tc:SAML:2.0:status:", string.Empty);
		}
	}

	/// <summary>
	/// Represents an IdP-initiated Logout Request received by the SP.
	/// </summary>
	public class IdpLogoutRequest : BaseSamlMessage
	{
		public IdpLogoutRequest(string certificateStr, string responseString = null) : base(certificateStr, responseString) { }

		public IdpLogoutRequest(byte[] certificateBytes, string responseString = null) : base(certificateBytes, responseString) { }

		/// <summary>
		/// Gets the NameID from the IdP-initiated LogoutRequest.
		/// </summary>
		public string GetNameID()
		{
			// LogoutRequest typically uses /samlp:LogoutRequest/saml:NameID
			XmlNode node = _xmlDoc.SelectSingleNode("/samlp:LogoutRequest/saml:NameID", _xmlNameSpaceManager);
			return node?.InnerText;
		}

		/// <summary>
		/// Gets the SessionIndex from the IdP-initiated LogoutRequest.
		/// </summary>
		/// <returns>The SessionIndex string, or null if not found.</returns>
		public string GetSessionIndex()
		{
			// SessionIndex is optional in the SAML spec for LogoutRequest
			XmlNode node = _xmlDoc.SelectSingleNode("/samlp:LogoutRequest/samlp:SessionIndex", _xmlNameSpaceManager);
			return node?.InnerText;
		}

		/// <summary>
		/// Checks the validity of the SAML IdP-initiated LogoutRequest (validate signature).
		/// This class relies on the base IsValid() method but overrides IsExpired() to always return false,
		/// effectively bypassing the expiration check which is not relevant for LogoutRequests.
		/// </summary>
		protected override bool IsExpired()
		{
			// LogoutRequests don't have the standard expiration elements.
			// Return false to ensure the base IsValid() check doesn't fail due to expiration.
			return false;
		}
	}

	public abstract class BaseRequest
	{
		public string _id;
		protected string _issue_instant;

		protected string _issuer;

        private X509Certificate2? _signingCertificate;

        public BaseRequest(string issuer)
		{
			_id = "_" + Guid.NewGuid().ToString();
			_issue_instant = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

			_issuer = issuer;
		}

        public BaseRequest(string issuer, X509Certificate2 certificate)
        {
            _id = "_" + Guid.NewGuid().ToString();
            _issue_instant = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

            _issuer = issuer;

			_signingCertificate = certificate;
        }

        public abstract string GetRequest();

        public abstract string GetSignedRequest(X509Certificate2 certificate);

        protected static string ConvertToBase64Deflated(string input)
		{
			//byte[] toEncodeAsBytes = System.Text.ASCIIEncoding.ASCII.GetBytes(input);
			//return System.Convert.ToBase64String(toEncodeAsBytes);

			//https://stackoverflow.com/questions/25120025/acs75005-the-request-is-not-a-valid-saml2-protocol-message-is-showing-always%3C/a%3E
			var memoryStream = new MemoryStream();
			using (var writer = new StreamWriter(new DeflateStream(memoryStream, CompressionMode.Compress, true), new UTF8Encoding(false)))
			{
				writer.Write(input);
				writer.Close();
			}
			string result = Convert.ToBase64String(memoryStream.GetBuffer(), 0, (int)memoryStream.Length, Base64FormattingOptions.None);
			return result;
		}

		/// <summary>
		/// returns the URL you should redirect your users to (i.e. your SAML-provider login URL with the Base64-ed request in the querystring
		/// </summary>
		/// <param name="samlEndpoint">SAML provider login url</param>
		/// <param name="relayState">Optional state to pass through</param>
		/// <returns></returns>
		public string GetRedirectUrl(string samlEndpoint, string relayState = null)
		{
			var queryStringSeparator = samlEndpoint.Contains("?") ? "&" : "?";
			string samlRequest = GetRequest();
			if (_signingCertificate != null)
			{
				samlRequest = GetSignedRequest(_signingCertificate);
            }
			else
                samlRequest = GetRequest();


            var url = samlEndpoint + queryStringSeparator + "SAMLRequest=" + Uri.EscapeDataString(samlRequest);

			if (!string.IsNullOrEmpty(relayState)) 
			{
				url += "&RelayState=" + Uri.EscapeDataString(relayState);
			}

			return url;
		}


        public static string SignAuthnRequest(string samlRequestXml, X509Certificate2 certificate)
        {
            XmlDocument xmlDoc = new XmlDocument();
            xmlDoc.PreserveWhitespace = true;
            xmlDoc.LoadXml(samlRequestXml);

            SignedXml signedXml = new SignedXml(xmlDoc);
            signedXml.SigningKey = certificate.PrivateKey;

            Reference reference = new Reference();
            reference.Uri = "#" + xmlDoc.DocumentElement.GetAttribute("ID");

            XmlDsigEnvelopedSignatureTransform env = new XmlDsigEnvelopedSignatureTransform();
            reference.AddTransform(env);

            signedXml.AddReference(reference);

            KeyInfo keyInfo = new KeyInfo();
            keyInfo.AddClause(new KeyInfoX509Data(certificate));
            signedXml.KeyInfo = keyInfo;

            signedXml.ComputeSignature();

            XmlElement xmlDigitalSignature = signedXml.GetXml();
            xmlDoc.DocumentElement.InsertBefore(xmlDigitalSignature, xmlDoc.DocumentElement.FirstChild);

            return xmlDoc.OuterXml;
        }

    }

    public class AuthRequest : BaseRequest
	{
		private string _assertionConsumerServiceUrl;
		

        
		/// <summary>
        /// Initializes new instance of AuthRequest
        /// </summary>
        /// <param name="issuer">put your EntityID here</param>
        /// <param name="assertionConsumerServiceUrl">put your return URL here</param>
        public AuthRequest(string issuer, string assertionConsumerServiceUrl) : base(issuer)
		{
			_assertionConsumerServiceUrl = assertionConsumerServiceUrl;
		}

        /// <summary>
        /// Initializes new instance of SIGNED AuthRequest
        /// </summary>
        /// <param name="issuer">put your EntityID here</param>
        /// <param name="assertionConsumerServiceUrl">put your return URL here</param>
		/// <param name="certificate">X509Certificate2 to sign the request</param>
        public AuthRequest(string issuer, string assertionConsumerServiceUrl, X509Certificate2 certificate) : base(issuer, certificate)
        {
			_assertionConsumerServiceUrl = assertionConsumerServiceUrl;
        }

        /// <summary>
        /// get or sets if ForceAuthn attribute is sent to IdP
        /// </summary>
        public bool ForceAuthn { get; set; }

		[Obsolete("Obsolete, will be removed")]
		public enum AuthRequestFormat
		{
			Base64 = 1
		}

		[Obsolete("Obsolete, will be removed, use GetRequest()")]
		public string GetRequest(AuthRequestFormat format) => GetRequest();

		/// <summary>
		/// returns SAML request as compressed and Base64 encoded XML. You don't need this method
		/// </summary>
		/// <returns></returns>
		public override string GetRequest()
		{
			using (StringWriter sw = new StringWriter())
			{
				XmlWriterSettings xws = new XmlWriterSettings { OmitXmlDeclaration = true };

				using (XmlWriter xw = XmlWriter.Create(sw, xws))
				{
					xw.WriteStartElement("samlp", "AuthnRequest", "urn:oasis:names:tc:SAML:2.0:protocol");
					xw.WriteAttributeString("ID", _id);
					xw.WriteAttributeString("Version", "2.0");
					xw.WriteAttributeString("IssueInstant", _issue_instant);
					xw.WriteAttributeString("ProtocolBinding", "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST");
					xw.WriteAttributeString("AssertionConsumerServiceURL", _assertionConsumerServiceUrl);
					if (ForceAuthn)
						xw.WriteAttributeString("ForceAuthn", "true");

					xw.WriteStartElement("saml", "Issuer", "urn:oasis:names:tc:SAML:2.0:assertion");
					xw.WriteString(_issuer);
					xw.WriteEndElement();

					xw.WriteStartElement("samlp", "NameIDPolicy", "urn:oasis:names:tc:SAML:2.0:protocol");
					xw.WriteAttributeString("Format", "urn:oasis:names:tc:SAML:1.1:nameid-format:unspecified");
					xw.WriteAttributeString("AllowCreate", "true");
					xw.WriteEndElement();

					xw.WriteStartElement("samlp", "RequestedAuthnContext", "urn:oasis:names:tc:SAML:2.0:protocol");
					xw.WriteAttributeString("Comparison", "exact");
					xw.WriteStartElement("saml", "AuthnContextClassRef", "urn:oasis:names:tc:SAML:2.0:assertion");
					xw.WriteString("urn:oasis:names:tc:SAML:2.0:ac:classes:Password");
					xw.WriteEndElement();
					xw.WriteEndElement();

					xw.WriteEndElement();
				}

				return ConvertToBase64Deflated(sw.ToString());
			}
		}


        /// <summary>
        /// returns SAML request as compressed and Base64 encoded XML. You don't need this method
        /// </summary>
        /// <returns></returns>
        public override string GetSignedRequest(X509Certificate2 certificate)
        {
            using (StringWriter sw = new StringWriter())
            {
                XmlWriterSettings xws = new XmlWriterSettings { OmitXmlDeclaration = true };

                using (XmlWriter xw = XmlWriter.Create(sw, xws))
                {
                    xw.WriteStartElement("samlp", "AuthnRequest", "urn:oasis:names:tc:SAML:2.0:protocol");
                    xw.WriteAttributeString("ID", _id);
                    xw.WriteAttributeString("Version", "2.0");
                    xw.WriteAttributeString("IssueInstant", _issue_instant);
                    xw.WriteAttributeString("ProtocolBinding", "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST");
                    xw.WriteAttributeString("AssertionConsumerServiceURL", _assertionConsumerServiceUrl);
                    if (ForceAuthn)
                        xw.WriteAttributeString("ForceAuthn", "true");

                    xw.WriteStartElement("saml", "Issuer", "urn:oasis:names:tc:SAML:2.0:assertion");
                    xw.WriteString(_issuer);
                    xw.WriteEndElement();

                    xw.WriteStartElement("samlp", "NameIDPolicy", "urn:oasis:names:tc:SAML:2.0:protocol");
                    xw.WriteAttributeString("Format", "urn:oasis:names:tc:SAML:1.1:nameid-format:unspecified");
                    xw.WriteAttributeString("AllowCreate", "true");
                    xw.WriteEndElement();

                    /*xw.WriteStartElement("samlp", "RequestedAuthnContext", "urn:oasis:names:tc:SAML:2.0:protocol");
					xw.WriteAttributeString("Comparison", "exact");
					xw.WriteStartElement("saml", "AuthnContextClassRef", "urn:oasis:names:tc:SAML:2.0:assertion");
					xw.WriteString("urn:oasis:names:tc:SAML:2.0:ac:classes:PasswordProtectedTransport");
					xw.WriteEndElement();
					xw.WriteEndElement();*/

                    xw.WriteEndElement();
                }

                // Sign the request
				string signedXml = SignAuthnRequest(sw.ToString(), certificate);


                return ConvertToBase64Deflated(signedXml);
            }
        }



    }

	/// <summary>
	/// Represents an SP-initiated Logout Request to be sent to the IdP.
	/// </summary>
	public class SignoutRequest : BaseRequest
	{
		private string _nameId;

		public SignoutRequest(string issuer, string nameId, X509Certificate2 certificate) : base(issuer, certificate)
		{
			_nameId = nameId;
		}
        public SignoutRequest(string issuer, string nameId) : base(issuer)
        {
            _nameId = nameId;
        }

        public override string GetRequest()
		{
			using (StringWriter sw = new StringWriter())
			{
				XmlWriterSettings xws = new XmlWriterSettings { OmitXmlDeclaration = true };

				using (XmlWriter xw = XmlWriter.Create(sw, xws))
				{
					xw.WriteStartElement("samlp", "LogoutRequest", "urn:oasis:names:tc:SAML:2.0:protocol");
					xw.WriteAttributeString("ID", _id);
					xw.WriteAttributeString("Version", "2.0");
					xw.WriteAttributeString("IssueInstant", _issue_instant);

					xw.WriteStartElement("saml", "Issuer", "urn:oasis:names:tc:SAML:2.0:assertion");
					xw.WriteString(_issuer);
					xw.WriteEndElement();

					xw.WriteStartElement("saml", "NameID", "urn:oasis:names:tc:SAML:2.0:assertion");
					xw.WriteString(_nameId);
					xw.WriteEndElement();

					xw.WriteEndElement();
				}

				return ConvertToBase64Deflated(sw.ToString());
			}
		}

        public override string GetSignedRequest(X509Certificate2 certificate)
        {
            using (StringWriter sw = new StringWriter())
            {
                XmlWriterSettings xws = new XmlWriterSettings { OmitXmlDeclaration = true };

                using (XmlWriter xw = XmlWriter.Create(sw, xws))
                {
                    xw.WriteStartElement("samlp", "LogoutRequest", "urn:oasis:names:tc:SAML:2.0:protocol");
                    xw.WriteAttributeString("ID", _id);
                    xw.WriteAttributeString("Version", "2.0");
                    xw.WriteAttributeString("IssueInstant", _issue_instant);

                    xw.WriteStartElement("saml", "Issuer", "urn:oasis:names:tc:SAML:2.0:assertion");
                    xw.WriteString(_issuer);
                    xw.WriteEndElement();

                    xw.WriteStartElement("saml", "NameID", "urn:oasis:names:tc:SAML:2.0:assertion");
                    xw.WriteString(_nameId);
                    xw.WriteEndElement();

                    xw.WriteEndElement();
                }


                // Sign the request
                string signedXml = SignAuthnRequest(sw.ToString(), certificate);

                return ConvertToBase64Deflated(signedXml);

            }
        }

    }

	public static class MetaData
	{
		/// <summary>
		/// generates XML string describing service provider metadata based on provided EntiytID and Consumer URL
		/// </summary>
		/// <param name="entityId">Your SP EntityID</param>
		/// <param name="assertionConsumerServiceUrl">Your Assertion Consumer Service URL (where IdP sends responses)</param>
		/// <param name="singleLogoutServiceUrl">Optional: Your Single Logout Service URL (where IdP sends LogoutRequests)</param>
		/// <returns>XML metadata string</returns>
		public static string Generate(string entityId, string assertionConsumerServiceUrl, string singleLogoutServiceUrl = null)
		{
			string sloServiceElement = "";
			if (!string.IsNullOrEmpty(singleLogoutServiceUrl))
			{
				// We advertise HTTP-POST binding as IdpLogoutRequest handles POST
				sloServiceElement = $@"
			<md:SingleLogoutService Binding=""urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST"" Location=""{singleLogoutServiceUrl}"" />";
			}

			// Construct the final metadata XML
			// NOTE: Using string interpolation with $@ can be tricky with complex XML and quotes.
			// Consider using XmlWriter or Linq to XML for more robust XML generation if needed.
			return $@"<?xml version=""1.0""?>
<md:EntityDescriptor xmlns:md=""urn:oasis:names:tc:SAML:2.0:metadata""
	validUntil=""{DateTime.UtcNow.AddYears(1).ToString("s")}Z"" 
	entityID=""{entityId}"">
	
	<md:SPSSODescriptor AuthnRequestsSigned=""false"" WantAssertionsSigned=""true"" protocolSupportEnumeration=""urn:oasis:names:tc:SAML:2.0:protocol"">
	
		<md:NameIDFormat>urn:oasis:names:tc:SAML:1.1:nameid-format:unspecified</md:NameIDFormat>

		<md:AssertionConsumerService Binding=""urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST""
			Location=""{assertionConsumerServiceUrl}""
			index=""1"" />{sloServiceElement}
	</md:SPSSODescriptor>
</md:EntityDescriptor>";
		}
	}
}