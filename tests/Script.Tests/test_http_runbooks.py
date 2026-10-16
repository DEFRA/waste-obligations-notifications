from pathlib import Path
import re
import unittest


ROOT = Path(__file__).resolve().parents[2]
METHODS = "Get|Post|Put|Patch|Delete|Head|Options"


class HttpRunbookTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        admin = ROOT / "src/Consumer/Endpoints/Admin"
        groups = re.findall(
            r'\.MapGroup\(\s*"([^"]+)"\s*\)\s*\.RequireAuthorization\(',
            (admin / "AdminEndpoints.cs").read_text(),
        )
        if len(groups) != 1:
            raise AssertionError("Update runbook route discovery for the admin group mapping")

        cls.routes = set()
        cls.authenticated = set()
        for path in admin.glob("*.cs"):
            source = path.read_text()
            mappings = re.findall(rf'\.Map({METHODS})\(\s*"([^"]+)"', source)
            if len(mappings) != len(re.findall(rf'\.Map(?:{METHODS}|Methods)\(', source)):
                raise AssertionError(f"Update runbook route discovery for {path.name}")
            for method, route in mappings:
                endpoint = (method.upper(), groups[0] + route)
                cls.routes.add(endpoint)
                cls.authenticated.add(endpoint)

        health = (ROOT / "src/Consumer/Utils/Health/WebApplicationExtensions.cs").read_text()
        mappings = re.findall(
            r'\.MapHealthChecks\(\s*"(/health[^"]*)".*?\)\s*'
            r'\.(AllowAnonymous|RequireAuthorization)\(',
            health,
            re.DOTALL,
        )
        if not mappings or len(mappings) != len(re.findall(r'\.MapHealthChecks\(', health)):
            raise AssertionError("Update runbook route discovery for health mappings")
        for route, access in mappings:
            endpoint = ("GET", route)
            cls.routes.add(endpoint)
            if access == "RequireAuthorization":
                cls.authenticated.add(endpoint)

        cls.requests = []
        for path in (ROOT / "runbooks/http").glob("*.http"):
            for block in path.read_text().split("###"):
                match = re.search(
                    r'^(GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS) \{\{host\}\}([^?\s]+)',
                    block,
                    re.MULTILINE,
                )
                if match:
                    cls.requests.append((match.groups(), block, path.name))

    def test_requests_match_mapped_admin_and_health_routes(self):
        self.assertTrue(self.authenticated, "No authenticated endpoints were discovered")
        self.assertSetEqual(self.routes, {endpoint for endpoint, _, _ in self.requests})

    def test_protected_requests_include_environment_authorization(self):
        for endpoint, block, filename in self.requests:
            if endpoint in self.authenticated:
                with self.subTest(endpoint=endpoint, file=filename):
                    self.assertIn("Authorization: {{authorization}}", block)
